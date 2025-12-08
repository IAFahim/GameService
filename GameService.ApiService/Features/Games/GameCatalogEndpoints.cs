using System.Security.Claims;
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

        app.MapGet("/games/lobby", GetPublicLobby)
            .RequireAuthorization()
            .WithName("GetPublicLobby");

        app.MapGet("/time", () => Results.Ok(new { ServerTime = DateTimeOffset.UtcNow }))
            .WithName("GetServerTime");

        app.MapPost("/games/quick-match", QuickMatch)
            .RequireAuthorization()
            .WithName("QuickMatch");
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
        if (string.IsNullOrEmpty(gameType))
            return Results.BadRequest("GameType is required");

        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);

        var (roomIds, _) = await registry.GetRoomIdsPagedAsync(gameType, (page - 1) * pageSize, pageSize);
        var engine = sp.GetKeyedService<IGameEngine>(gameType);

        if (engine == null || roomIds.Count == 0)
            return Results.Ok(new List<GameRoomDto>());

        var states = await engine.GetManyStatesAsync(roomIds.ToList());

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

    private static async Task<IResult> QuickMatch(
        [FromBody] QuickMatchRequest req,
        IRoomRegistry registry,
        IServiceProvider sp,
        HttpContext ctx)
    {
        var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var (roomIds, _) = await registry.GetRoomIdsPagedAsync(req.GameType, 0, 50);
        var engine = sp.GetKeyedService<IGameEngine>(req.GameType);

        if (engine != null && roomIds.Count > 0)
        {
            var states = await engine.GetManyStatesAsync(roomIds.ToList());

            var bestMatch = states.FirstOrDefault(s =>
                s.Meta.IsPublic &&
                s.Meta.CurrentPlayerCount < s.Meta.MaxPlayers);

            if (bestMatch != null)
            {
                return Results.Ok(new QuickMatchResponse(bestMatch.RoomId, "Join"));
            }
        }

        var roomService = sp.GetKeyedService<IGameRoomService>(req.GameType);
        if (roomService == null)
            return Results.BadRequest("Unsupported game type");

        var meta = new GameRoomMeta
        {
            GameType = req.GameType,
            MaxPlayers = req.MaxPlayers,
            EntryFee = req.EntryFee,
            IsPublic = true,
            Config = new Dictionary<string, string>()
        };

        var newRoomId = await roomService.CreateRoomAsync(meta);

        await registry.SetUserRoomAsync(userId, newRoomId);

        return Results.Ok(new QuickMatchResponse(newRoomId, "Created"));
    }
}