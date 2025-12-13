using System.Security.Claims;
using System.Text.Json;
using GameService.GameCore;
using GameService.ServiceDefaults.Data;
using GameService.ServiceDefaults.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GameService.ApiService.Features.Games;

public static class GameCatalogEndpoints
{
    public static void MapGameCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/games/supported", GetSupportedGames)
            .WithName("GetSupportedGames");

        app.MapGet("/games/templates", GetPublicTemplates)
            .RequireAuthorization() 
            .WithName("GetPublicTemplates");

        app.MapPost("/games/rooms", CreateRoomFromTemplate)
            .RequireAuthorization()
            .WithName("CreateRoomFromTemplate");

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
        var games = modules.Select(m => new SupportedGameDto(m.GameName, m.GameName)).ToList();
        return Results.Ok(games);
    }

    private static async Task<IResult> GetPublicTemplates(GameDbContext db)
    {
        var templates = await db.RoomTemplates
            .AsNoTracking()
            .Select(t => new GameTemplateDto(t.Id, t.Name, t.GameType, t.MaxPlayers, t.EntryFee, null))
            .ToListAsync();
        
        return Results.Ok(templates);
    }

    private static async Task<IResult> CreateRoomFromTemplate(
        [FromBody] CreateRoomFromTemplateRequest req,
        GameDbContext db,
        IServiceProvider sp,
        IRoomRegistry registry,
        HttpContext ctx)
    {
        var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var template = await db.RoomTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == req.TemplateId);
        if (template == null) return Results.NotFound("Template not found");

        var roomService = sp.GetKeyedService<IGameRoomService>(template.GameType);
        if (roomService == null) return Results.BadRequest("Game type not supported");

        var configDict = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(template.ConfigJson))
        {
            try {
                var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(template.ConfigJson);
                if (dict != null) foreach (var kvp in dict) configDict[kvp.Key] = kvp.Value?.ToString() ?? "";
            } catch {
            }
        }

        var meta = new GameRoomMeta
        {
            GameType = template.GameType,
            MaxPlayers = template.MaxPlayers,
            EntryFee = template.EntryFee,
            Config = configDict,
            IsPublic = true,
            PlayerSeats = new Dictionary<string, int> { [userId] = 0 } 
        };

        var roomId = await roomService.CreateRoomAsync(meta);

        await registry.SetUserRoomAsync(userId, roomId);

        var shortCode = await registry.RegisterShortCodeAsync(roomId);

        return Results.Ok(new { RoomId = roomId, ShortCode = shortCode, GameType = template.GameType });
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
        HttpContext ctx,
        GameDbContext db)
    {
        var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        string targetGameType = req.GameType;
        int targetMaxPlayers = req.MaxPlayers;
        long targetEntryFee = req.EntryFee;
        Dictionary<string, string> targetConfig = new();

        if (req.TemplateId.HasValue)
        {
            var template = await db.RoomTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == req.TemplateId.Value);
            if (template == null) return Results.NotFound("Template not found");

            targetGameType = template.GameType;
            targetMaxPlayers = template.MaxPlayers;
            targetEntryFee = template.EntryFee;

            if (!string.IsNullOrEmpty(template.ConfigJson))
            {
                try
                {
                    var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(template.ConfigJson);
                    if (dict != null) foreach (var kvp in dict) targetConfig[kvp.Key] = kvp.Value?.ToString() ?? "";
                }
                catch {
                }
            }
        }

        var (roomIds, _) = await registry.GetRoomIdsPagedAsync(targetGameType, 0, 50);
        var engine = sp.GetKeyedService<IGameEngine>(targetGameType);

        if (engine != null && roomIds.Count > 0)
        {
            var states = await engine.GetManyStatesAsync(roomIds.ToList());

            var bestMatch = states.FirstOrDefault(s =>
                s.Meta.IsPublic &&
                s.Meta.CurrentPlayerCount < s.Meta.MaxPlayers &&
                s.Meta.MaxPlayers == targetMaxPlayers &&
                s.Meta.EntryFee == targetEntryFee);

            if (bestMatch != null)
            {
                return Results.Ok(new QuickMatchResponse(bestMatch.RoomId, "Join"));
            }
        }

        var roomService = sp.GetKeyedService<IGameRoomService>(targetGameType);
        if (roomService == null)
            return Results.BadRequest("Unsupported game type");

        var meta = new GameRoomMeta
        {
            GameType = targetGameType,
            MaxPlayers = targetMaxPlayers,
            EntryFee = targetEntryFee,
            IsPublic = true,
            Config = targetConfig
        };

        var newRoomId = await roomService.CreateRoomAsync(meta);

        await registry.SetUserRoomAsync(userId, newRoomId);

        return Results.Ok(new QuickMatchResponse(newRoomId, "Created"));
    }
}