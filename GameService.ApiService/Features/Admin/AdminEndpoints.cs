using System.Text.Json;
using GameService.ApiService.Features.Common;
using GameService.ServiceDefaults.Data;
using GameService.ServiceDefaults.DTOs;
using GameService.GameCore;
using GameService.ServiceDefaults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GameService.ApiService.Features.Admin;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin").RequireAuthorization("AdminPolicy");

        group.MapGet("/templates", GetTemplates);
        group.MapPost("/templates", CreateTemplate);
        group.MapDelete("/templates/{id}", DeleteTemplate);

        group.MapPost("/games", CreateAdHocGame); 
        group.MapPost("/games/create-from-template", CreateGameFromTemplate);
        group.MapGet("/games", GetGames);
        group.MapGet("/games/{roomId}", GetGameState);
        group.MapDelete("/games/{roomId}", DeleteGame);

        group.MapGet("/players", GetPlayers);
        group.MapPost("/players/{userId}/coins", UpdatePlayerCoins);
        group.MapDelete("/players/{userId}", DeletePlayer);
    }

    private static async Task<IResult> GetTemplates(GameDbContext db)
    {
        var templates = await db.RoomTemplates.AsNoTracking().ToListAsync();
        return Results.Ok(templates.Select(t => new GameTemplateDto(t.Id, t.Name, t.GameType, t.MaxPlayers, t.EntryFee, t.ConfigJson)));
    }

    private static async Task<IResult> CreateTemplate([FromBody] CreateTemplateRequest req, GameDbContext db)
    {
        var template = new GameRoomTemplate { Name = req.Name, GameType = req.GameType, MaxPlayers = req.MaxPlayers, EntryFee = req.EntryFee, ConfigJson = req.ConfigJson };
        db.RoomTemplates.Add(template);
        await db.SaveChangesAsync();
        return Results.Ok(template.Id);
    }

    private static async Task<IResult> DeleteTemplate(int id, GameDbContext db)
    {
        await db.RoomTemplates.Where(t => t.Id == id).ExecuteDeleteAsync();
        return Results.Ok();
    }

    private static async Task<IResult> CreateGameFromTemplate(
        [FromBody] CreateRoomFromTemplateRequest req,
        GameDbContext db,
        IServiceProvider sp)
    {
        var template = await db.RoomTemplates.FindAsync(req.TemplateId);
        if (template == null) return Results.NotFound("Template not found");

        return await CreateGameInternal(sp, template.GameType, template.MaxPlayers, template.EntryFee, template.ConfigJson);
    }

    private static async Task<IResult> CreateAdHocGame(
        [FromBody] CreateGameRequest req,
        IServiceProvider sp)
    {
        return await CreateGameInternal(sp, req.GameType, req.PlayerCount, req.EntryFee, req.ConfigJson); 
    }

    private static async Task<IResult> CreateGameInternal(
        IServiceProvider sp, 
        string gameType, 
        int maxPlayers, 
        long entryFee, 
        string? configJson)
    {
        if (string.IsNullOrWhiteSpace(gameType))
            return Results.BadRequest("Game type is required");
        
        if (maxPlayers < 1 || maxPlayers > 100)
            return Results.BadRequest("Max players must be between 1 and 100");

        var roomService = sp.GetKeyedService<IGameRoomService>(gameType);
        if (roomService == null) return Results.BadRequest($"Game type '{gameType}' not supported");

        var logger = sp.GetRequiredService<ILogger<IGameRoomService>>();
        var configDict = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(configJson))
        {
            try 
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(configJson);
                if (dict != null)
                {
                    foreach(var kvp in dict) configDict[kvp.Key] = kvp.Value?.ToString() ?? "";
                }
            } 
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Invalid JSON config provided for game type {GameType}", gameType);
                return Results.BadRequest("Invalid configuration JSON format");
            }
        }

        var metaConfig = new GameRoomMeta
        {
            GameType = gameType,
            MaxPlayers = maxPlayers,
            EntryFee = entryFee,
            Config = configDict,
            IsPublic = true
        };

        var roomId = await roomService.CreateRoomAsync(metaConfig);
        return Results.Ok(new { RoomId = roomId, GameType = gameType });
    }

    private static async Task<IResult> GetGameState(string roomId, IServiceProvider sp, IRoomRegistry registry)
    {
        var gameType = await registry.GetGameTypeAsync(roomId);
        if (gameType == null) return Results.NotFound($"Room '{roomId}' not found");
        var engine = sp.GetKeyedService<IGameEngine>(gameType);
        if (engine == null) return Results.NotFound($"Game engine for '{gameType}' not available");
        var state = await engine.GetStateAsync(roomId);
        return state != null ? Results.Ok(state) : Results.NotFound();
    }

    private static async Task<IResult> DeleteGame(string roomId, IServiceProvider sp, IRoomRegistry registry)
    {
        var gameType = await registry.GetGameTypeAsync(roomId);
        if (gameType == null) return Results.NotFound($"Room '{roomId}' not found");
        var roomService = sp.GetKeyedService<IGameRoomService>(gameType);
        if (roomService != null) await roomService.DeleteRoomAsync(roomId);
        return Results.Ok(new { RoomId = roomId, Deleted = true });
    }

    private static async Task<IResult> GetGames(IEnumerable<IGameModule> modules, IServiceProvider sp, IRoomRegistry registry, [FromQuery] string? gameType = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        if (pageSize > 100) pageSize = 100;
        if (page < 1) page = 1;
        var allGames = new List<GameRoomDto>();
        var modulesToQuery = string.IsNullOrEmpty(gameType) ? modules : modules.Where(m => m.GameName.Equals(gameType, StringComparison.OrdinalIgnoreCase));
        foreach (var module in modulesToQuery)
        {
            var engine = sp.GetKeyedService<IGameEngine>(module.GameName);
            if (engine == null) continue;
            var cursor = (long)(page - 1) * pageSize;
            var (roomIds, _) = await registry.GetRoomIdsPagedAsync(module.GameName, cursor, pageSize);
            foreach (var roomId in roomIds)
            {
                var state = await engine.GetStateAsync(roomId);
                if (state != null) allGames.Add(new GameRoomDto(state.RoomId, state.GameType, state.Meta.CurrentPlayerCount, state.Meta.MaxPlayers, state.Meta.IsPublic, state.Meta.PlayerSeats));
                if (allGames.Count >= pageSize) break;
            }
            if (allGames.Count >= pageSize) break;
        }
        return Results.Ok(allGames);
    }

    private static async Task<IResult> GetPlayers(GameDbContext db, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var players = await db.PlayerProfiles.AsNoTracking().Include(p => p.User).OrderBy(p => p.Id).Skip((page - 1) * pageSize).Take(pageSize).Select(p => new AdminPlayerDto(p.Id, p.UserId, p.User.UserName ?? "Unknown", p.User.Email ?? "No Email", p.Coins)).ToListAsync();
        return Results.Ok(players);
    }

    private static async Task<IResult> UpdatePlayerCoins(string userId, [FromBody] UpdateCoinRequest req, GameDbContext db, IGameEventPublisher publisher)
    {
        var rows = await db.PlayerProfiles.Where(p => p.UserId == userId).ExecuteUpdateAsync(setters => setters.SetProperty(p => p.Coins, p => p.Coins + req.Amount).SetProperty(p => p.Version, Guid.NewGuid()));
        if (rows == 0) return Results.NotFound();
        var profile = await db.PlayerProfiles.Include(p => p.User).AsNoTracking().FirstAsync(p => p.UserId == userId);
        await publisher.PublishPlayerUpdatedAsync(new PlayerUpdatedMessage(profile.UserId, profile.Coins, profile.User?.UserName, profile.User?.Email));
        return Results.Ok(new { NewBalance = profile.Coins });
    }

    private static async Task<IResult> DeletePlayer(string userId, UserManager<ApplicationUser> userManager, IGameEventPublisher publisher)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user == null) return Results.NotFound();
        var result = await userManager.DeleteAsync(user);
        if (!result.Succeeded) return Results.BadRequest(result.Errors);
        await publisher.PublishPlayerUpdatedAsync(new PlayerUpdatedMessage(userId, 0, user.UserName, user.Email, PlayerChangeType.Deleted));
        return Results.Ok();
    }

    public record CreateGameRequest(string GameType, int PlayerCount, long EntryFee = 0, string? ConfigJson = null);
}