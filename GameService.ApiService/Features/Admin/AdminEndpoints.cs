using System.Security.Claims;
using GameService.ApiService.Features.Common;
using GameService.Ludo;
using GameService.ServiceDefaults.Data;
using GameService.ServiceDefaults.DTOs;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GameService.ApiService.Features.Admin;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin").RequireAuthorization("AdminPolicy");

        group.MapGet("/games", GetGames);
        group.MapPost("/games", CreateGame);
        group.MapPost("/games/{roomId}/roll", ForceRoll);
        group.MapDelete("/games/{roomId}", DeleteGame);

        group.MapGet("/players", GetPlayers);
        group.MapPost("/players/{userId}/coins", UpdatePlayerCoins);
        group.MapDelete("/players/{userId}", DeletePlayer);
    }

    private static async Task<IResult> GetPlayers(GameDbContext db)
    {
        var players = await db.PlayerProfiles
            .AsNoTracking()
            .Include(p => p.User)
            .OrderBy(p => p.Id)
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
        var profile = await db.PlayerProfiles.Include(p => p.User).FirstOrDefaultAsync(p => p.UserId == userId);
        if (profile == null) return Results.NotFound();

        try
        {
            checked
            {
                profile.Coins += req.Amount;
            }
        }
        catch (OverflowException)
        {
            return Results.BadRequest("Coin overflow/underflow");
        }

        await db.SaveChangesAsync();

        var message = new PlayerUpdatedMessage(
            profile.UserId, 
            profile.Coins, 
            profile.User?.UserName ?? "Unknown", 
            profile.User?.Email ?? "Unknown");

        await publisher.PublishPlayerUpdatedAsync(message);

        return Results.Ok(new { NewBalance = profile.Coins });
    }

    private static async Task<IResult> DeletePlayer(
        string userId, 
        UserManager<ApplicationUser> userManager)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user == null) return Results.NotFound();

        var result = await userManager.DeleteAsync(user);
        if (!result.Succeeded) return Results.BadRequest(result.Errors);

        return Results.Ok();
    }

    private static async Task<IResult> GetGames(LudoRoomService service)
    {
        var games = await service.GetActiveGamesAsync();
        return Results.Ok(games);
    }

    private static async Task<IResult> CreateGame(
        LudoRoomService service, 
        ClaimsPrincipal user,
        IHubContext<LudoHub> hub)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var roomId = await service.CreateRoomAsync(userId);
        return Results.Ok(new { RoomId = roomId });
    }

    private static async Task<IResult> ForceRoll(
        string roomId, 
        [FromQuery] int value, 
        LudoRoomService service,
        IHubContext<LudoHub> hub)
    {
        var ctx = await service.LoadGameAsync(roomId);
        if (ctx == null) return Results.NotFound();

        if (ctx.Engine.TryRollDice(out var result, (byte)value))
        {
            await service.SaveGameAsync(ctx);

            // Notify clients via SignalR
            await hub.Clients.Group(roomId).SendAsync("RollResult", result.DiceValue);
            
            // Always send state update after a forced move/roll
            await hub.Clients.Group(roomId).SendAsync("GameState", SerializeState(ctx.Engine.State));

            return Results.Ok(new { result.Status, result.DiceValue });
        }

        return Results.BadRequest("Could not roll dice (maybe game ended or need to move token first)");
    }

    private static async Task<IResult> DeleteGame(string roomId, LudoRoomService service, IHubContext<LudoHub> hub)
    {
        await service.DeleteRoomAsync(roomId);
        await hub.Clients.Group(roomId).SendAsync("GameDeleted");
        return Results.Ok();
    }

    private static unsafe byte[] SerializeState(LudoState state)
    {
        var bytes = new byte[sizeof(LudoState)];
        fixed (byte* b = bytes) { *(LudoState*)b = state; }
        return bytes;
    }
}