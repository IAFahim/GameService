using System.Security.Claims;
using GameService.Ludo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace GameService.ApiService.Features.Admin;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/games"); //.RequireAuthorization("AdminPolicy"); // TODO: Add Admin Policy

        group.MapGet("/", GetGames);
        group.MapPost("/", CreateGame);
        group.MapPost("/{roomId}/roll", ForceRoll);
        group.MapDelete("/{roomId}", DeleteGame);
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