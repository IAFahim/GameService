using GameService.ApiService.Features.Common;
using GameService.ServiceDefaults;
using GameService.GameCore;
using GameService.ServiceDefaults.Data;
using GameService.ServiceDefaults.DTOs;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GameService.ApiService.Features.Admin;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin").RequireAuthorization("AdminPolicy");

        // Game management - now with O(1) lookups
        group.MapPost("/games", CreateGame);
        group.MapGet("/games", GetGames);
        group.MapGet("/games/{roomId}", GetGameState);
        group.MapDelete("/games/{roomId}", DeleteGame);
        
        // Player management
        group.MapGet("/players", GetPlayers);
        group.MapPost("/players/{userId}/coins", UpdatePlayerCoins);
        group.MapDelete("/players/{userId}", DeletePlayer);
    }

    /// <summary>
    /// Create a game room - O(1) service lookup using keyed services
    /// </summary>
    private static async Task<IResult> CreateGame(
        [FromBody] CreateGameRequest req,
        IServiceProvider sp,
        IRoomRegistry registry)
    {
        // O(1) lookup using keyed service
        var roomService = sp.GetKeyedService<IGameRoomService>(req.GameType);
        if (roomService == null)
        {
            return Results.BadRequest($"Game type '{req.GameType}' not supported");
        }

        var roomId = await roomService.CreateRoomAsync(null, req.PlayerCount);
        return Results.Ok(new { RoomId = roomId, GameType = req.GameType });
    }

    /// <summary>
    /// Get game state - O(1) room registry lookup then O(1) service lookup
    /// </summary>
    private static async Task<IResult> GetGameState(
        string roomId, 
        IServiceProvider sp,
        IRoomRegistry registry)
    {
        // O(1) lookup: What game type is this room?
        var gameType = await registry.GetGameTypeAsync(roomId);
        if (gameType == null)
        {
            return Results.NotFound($"Room '{roomId}' not found");
        }

        // O(1) lookup: Get the correct engine
        var engine = sp.GetKeyedService<IGameEngine>(gameType);
        if (engine == null)
        {
            return Results.NotFound($"Game engine for '{gameType}' not available");
        }

        var state = await engine.GetStateAsync(roomId);
        return state != null ? Results.Ok(state) : Results.NotFound();
    }

    /// <summary>
    /// Delete a game room - O(1) lookups
    /// </summary>
    private static async Task<IResult> DeleteGame(
        string roomId, 
        IServiceProvider sp,
        IRoomRegistry registry)
    {
        var gameType = await registry.GetGameTypeAsync(roomId);
        if (gameType == null)
        {
            return Results.NotFound($"Room '{roomId}' not found");
        }

        var roomService = sp.GetKeyedService<IGameRoomService>(gameType);
        if (roomService != null)
        {
            await roomService.DeleteRoomAsync(roomId);
        }

        return Results.Ok(new { RoomId = roomId, Deleted = true });
    }

    /// <summary>
    /// Get all active games - iterates game types, not rooms
    /// </summary>
    private static async Task<IResult> GetGames(
        IEnumerable<IGameModule> modules,
        IServiceProvider sp,
        IRoomRegistry registry)
    {
        var allGames = new List<GameRoomDto>();

        // Iterate by game type (small number), not by room (large number)
        foreach (var module in modules)
        {
            var roomIds = await registry.GetRoomIdsByGameTypeAsync(module.GameName);
            var engine = sp.GetKeyedService<IGameEngine>(module.GameName);
            
            if (engine == null) continue;

            foreach (var roomId in roomIds)
            {
                var state = await engine.GetStateAsync(roomId);
                if (state != null)
                {
                    allGames.Add(new GameRoomDto(
                        state.RoomId,
                        state.GameType,
                        state.Meta.CurrentPlayerCount,
                        state.Meta.MaxPlayers,
                        state.Meta.IsPublic,
                        state.Meta.PlayerSeats
                    ));
                }
            }
        }

        return Results.Ok(allGames);
    }

    private static async Task<IResult> GetPlayers(
        GameDbContext db,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        if (pageSize > 100) pageSize = 100;
        if (page < 1) page = 1;

        var players = await db.PlayerProfiles
            .AsNoTracking()
            .Include(p => p.User)
            .OrderBy(p => p.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new AdminPlayerDto(
                p.Id,
                p.UserId,
                p.User.UserName ?? "Unknown",
                p.User.Email ?? "No Email",
                p.Coins
            ))
            .ToListAsync();

        return Results.Ok(players);
    }

    private static async Task<IResult> UpdatePlayerCoins(
        string userId, 
        [FromBody] UpdateCoinRequest req,
        GameDbContext db,
        IGameEventPublisher publisher)
    {
        var rows = await db.PlayerProfiles
            .Where(p => p.UserId == userId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(p => p.Coins, p => p.Coins + req.Amount)
                .SetProperty(p => p.Version, Guid.NewGuid()));

        if (rows == 0) return Results.NotFound();

        var profile = await db.PlayerProfiles.Include(p => p.User).AsNoTracking().FirstAsync(p => p.UserId == userId);

        var message = new PlayerUpdatedMessage(
            profile.UserId, 
            profile.Coins, 
            profile.User?.UserName ?? "Unknown", 
            profile.User?.Email ?? "Unknown",
            PlayerChangeType.Updated,
            profile.Id);

        await publisher.PublishPlayerUpdatedAsync(message);
        return Results.Ok(new { NewBalance = profile.Coins });
    }

    private static async Task<IResult> DeletePlayer(
        string userId, 
        UserManager<ApplicationUser> userManager,
        IGameEventPublisher publisher)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user == null) return Results.NotFound();

        var result = await userManager.DeleteAsync(user);
        if (!result.Succeeded) return Results.BadRequest(result.Errors);

        var message = new PlayerUpdatedMessage(
            userId, 
            0, 
            user.UserName, 
            user.Email, 
            PlayerChangeType.Deleted,
            0);

        await publisher.PublishPlayerUpdatedAsync(message);

        return Results.Ok();
    }
    
    public record CreateGameRequest(string GameType, int PlayerCount);
}