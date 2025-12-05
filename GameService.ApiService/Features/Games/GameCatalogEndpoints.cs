using GameService.GameCore;
using GameService.ServiceDefaults.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace GameService.ApiService.Features.Games;

public static class GameCatalogEndpoints
{
    public static void MapGameCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/games/supported", GetSupportedGames)
            .WithName("GetSupportedGames");

        // QoL: Public lobby endpoint for players to browse open rooms
        app.MapGet("/games/lobby", GetPublicLobby)
            .RequireAuthorization()
            .WithName("GetPublicLobby");

        // QoL: Server time synchronization for turn timers
        app.MapGet("/time", () => Results.Ok(new { ServerTime = DateTimeOffset.UtcNow }))
            .WithName("GetServerTime");
    }

    private static IResult GetSupportedGames(IEnumerable<IGameModule> modules)
    {
        var games = modules.Select(m => new SupportedGameDto(m.GameName)).ToList();
        return Results.Ok(games);
    }

    private static async Task<IResult> GetPublicLobby(
        [FromQuery] string? gameType,
        IRoomRegistry registry,
        IServiceProvider sp,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        // Filter by game type is mandatory for performance in Redis
        if (string.IsNullOrEmpty(gameType))
            return Results.BadRequest("GameType is required");

        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);

        var (roomIds, _) = await registry.GetRoomIdsPagedAsync(gameType, (page - 1) * pageSize, pageSize);
        var engine = sp.GetKeyedService<IGameEngine>(gameType);

        if (engine == null || roomIds.Count == 0)
            return Results.Ok(new List<GameRoomDto>());

        var states = await engine.GetManyStatesAsync(roomIds.ToList());

        // Filter: Must be public and have empty seats
        var lobby = states
            .Where(s => s.Meta.IsPublic && s.Meta.CurrentPlayerCount < s.Meta.MaxPlayers)
            .Select(s => new GameRoomDto(
                s.RoomId,
                s.GameType,
                s.Meta.CurrentPlayerCount,
                s.Meta.MaxPlayers,
                s.Meta.IsPublic,
                s.Meta.PlayerSeats
            ))
            .ToList();

        return Results.Ok(lobby);
    }
}