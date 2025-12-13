using System.Security.Claims;
using System.Text.Json;
using GameService.ApiService.Hubs;
using GameService.GameCore;
using GameService.ServiceDefaults;
using GameService.ServiceDefaults.Data;
using GameService.ServiceDefaults.DTOs;
using GameService.ServiceDefaults.Security;
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

        group.MapGet("/stats", GetDashboardStats);
        group.MapPost("/broadcast", SendSystemBroadcast);

        group.MapGet("/templates", GetTemplates);
        group.MapPost("/templates", CreateTemplate);
        group.MapDelete("/templates/{id:int}", DeleteTemplate);

        group.MapPost("/games", CreateAdHocGame);
        group.MapPost("/games/create-from-template", CreateGameFromTemplate);
        group.MapGet("/games", GetGames);
        group.MapGet("/games/{roomId}", GetGameState);
        group.MapGet("/games/{roomId}/players", GetGamePlayers);
        group.MapDelete("/games/{roomId}", DeleteGame);

        group.MapGet("/players", GetPlayers);
        group.MapGet("/players/{userId}/history", GetPlayerHistory);
        group.MapPost("/players/{userId}/coins", UpdatePlayerCoins);
        group.MapPut("/players/{userId}/profile", UpdatePlayerProfile);
        group.MapDelete("/players/{userId}", DeletePlayer);

        group.MapGet("/analytics/daily-login", GetDailyLoginAnalytics);
        group.MapGet("/analytics/daily-spin", GetDailySpinAnalytics);

        group.MapGet("/settings", GetSettings);
        group.MapPut("/settings", UpdateSetting);
    }

    private static async Task<IResult> GetGamePlayers(
        string roomId,
        IServiceProvider sp,
        IRoomRegistry registry,
        GameDbContext db)
    {
        if (!InputValidator.IsValidRoomId(roomId))
            return Results.BadRequest("Invalid room ID format");

        var gameType = await registry.GetGameTypeAsync(roomId);
        if (gameType == null) return Results.NotFound("Room not found");

        var engine = sp.GetKeyedService<IGameEngine>(gameType);
        if (engine == null) return Results.NotFound("Game engine not available");

        var state = await engine.GetStateAsync(roomId);
        if (state == null) return Results.NotFound("Game state not found");

        var playerIds = state.Meta.PlayerSeats.Keys.ToList();
        var users = await db.Users
            .AsNoTracking()
            .Where(u => playerIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.UserName);

        var players = state.Meta.PlayerSeats.Select(kvp => new GamePlayerDto(
            kvp.Key,
            users.TryGetValue(kvp.Key, out var name) ? name ?? "Unknown" : "Unknown",
            kvp.Value,
            false // Bots not yet distinguished in seats, assuming human for now
        )).ToList();

        return Results.Ok(players);
    }

    private static async Task<IResult> UpdatePlayerProfile(
        string userId,
        [FromBody] AdminUpdateProfileRequest req,
        GameDbContext db,
        UserManager<ApplicationUser> userManager,
        IGameEventPublisher publisher,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("AdminEndpoints");
        
        var user = await db.Users.Include(u => u.Profile).FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null) return Results.NotFound();

        bool changed = false;

        if (!string.IsNullOrWhiteSpace(req.DisplayName) && user.UserName != req.DisplayName)
        {
            user.UserName = req.DisplayName;
            user.NormalizedUserName = userManager.KeyNormalizer.NormalizeName(req.DisplayName);
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(req.Email) && user.Email != req.Email)
        {
            user.Email = req.Email;
            user.NormalizedEmail = userManager.KeyNormalizer.NormalizeEmail(req.Email);
            changed = true;
        }

        if (req.AvatarId.HasValue && user.Profile != null && user.Profile.AvatarId != req.AvatarId)
        {
            user.Profile.AvatarId = req.AvatarId.Value;
            changed = true;
        }

        if (changed)
        {
            await db.SaveChangesAsync();
            logger.LogInformation("Admin updated profile for {UserId}", userId);
            
            if (user.Profile != null)
            {
                await publisher.PublishPlayerUpdatedAsync(new PlayerUpdatedMessage(
                    user.Id, 
                    user.Profile.Coins, 
                    user.UserName, 
                    user.Email));
            }
        }

        return Results.Ok(new { Message = "Profile updated" });
    }

    private static async Task<IResult> GetDailyLoginAnalytics(GameDbContext db)
    {
        var data = await db.PlayerProfiles
            .AsNoTracking()
            .Where(p => !p.IsDeleted && p.DailyLoginStreak > 0)
            .GroupBy(p => p.DailyLoginStreak)
            .Select(g => new { Streak = g.Key, Count = g.Count(), SampleIds = g.Take(5).Select(p => p.UserId).ToList() })
            .OrderBy(x => x.Streak)
            .ToListAsync();

        var result = data.Select(x => new DailyLoginAnalyticsDto(x.Streak, x.Count, x.SampleIds)).ToList();
        return Results.Ok(result);
    }

    private static async Task<IResult> GetDailySpinAnalytics(GameDbContext db)
    {
        var totalSpins = await db.WalletTransactions.CountAsync(t => t.TransactionType == "DAILY_SPIN");
        if (totalSpins == 0) return Results.Ok(new List<DailySpinAnalyticsDto>());

        var data = await db.WalletTransactions
            .AsNoTracking()
            .Where(t => t.TransactionType == "DAILY_SPIN")
            .GroupBy(t => new { t.Currency, t.Amount })
            .Select(g => new { g.Key.Currency, g.Key.Amount, Count = g.Count() })
            .OrderBy(x => x.Currency).ThenBy(x => x.Amount)
            .ToListAsync();

        var result = data.Select(x => new DailySpinAnalyticsDto(
            x.Currency,
            x.Amount, 
            x.Count, 
            Math.Round((double)x.Count / totalSpins * 100, 2)
        )).ToList();

        return Results.Ok(result);
    }

    private static async Task<IResult> GetSettings(GameDbContext db)
    {
        var settings = await db.GlobalSettings.AsNoTracking().ToListAsync();
        return Results.Ok(settings);
    }

    private static async Task<IResult> UpdateSetting(
        [FromBody] UpdateSettingRequest req,
        GameDbContext db,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("AdminEndpoints");

        if (string.IsNullOrWhiteSpace(req.Key)) return Results.BadRequest("Key is required");
        if (req.Value == null) return Results.BadRequest("Value is required");

        var setting = await db.GlobalSettings.FindAsync(req.Key);
        if (setting == null)
        {
            setting = new GlobalSetting { Key = req.Key, Value = req.Value, Description = req.Description };
            db.GlobalSettings.Add(setting);
        }
        else
        {
            setting.Value = req.Value;
            if (req.Description != null) setting.Description = req.Description;
        }

        await db.SaveChangesAsync();
        logger.LogInformation("Setting updated: {Key} = {Value}", req.Key, req.Value);
        return Results.Ok(setting);
    }

    private static async Task<IResult> GetDashboardStats(
        GameDbContext db, 
        IRoomRegistry registry)
    {
        var onlineTask = registry.GetOnlinePlayerCountAsync();
        var roomsTask = registry.GetAllRoomIdsAsync();

        var usersCount = await db.Users.CountAsync();
        var totalCoins = await db.PlayerProfiles.SumAsync(p => p.Coins);

        await Task.WhenAll(onlineTask, roomsTask);

        var stats = new DashboardStatsDto(
            (int)onlineTask.Result,
            roomsTask.Result.Count,
            usersCount,
            totalCoins
        );

        return Results.Ok(stats);
    }

    private static async Task<IResult> SendSystemBroadcast(
        [FromBody] BroadcastRequest req,
        IHubContext<GameHub, IGameClient> hubContext,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("AdminEndpoints");
        
        if (string.IsNullOrWhiteSpace(req.Message))
            return Results.BadRequest("Message cannot be empty");

        var payload = new ChatMessagePayload(
            GameCoreConstants.SystemUserId, 
            "SYSTEM", 
            req.Message, 
            DateTimeOffset.UtcNow);

        await hubContext.Clients.All.ChatMessage(payload);

        logger.LogInformation("Admin broadcast sent: {Message}", req.Message);
        return Results.Ok(new { Sent = true });
    }

    private static async Task<IResult> GetTemplates(GameDbContext db)
    {
        var templates = await db.RoomTemplates.AsNoTracking().ToListAsync();
        return Results.Ok(templates.Select(t =>
            new GameTemplateDto(t.Id, t.Name, t.GameType, t.MaxPlayers, t.EntryFee, t.ConfigJson)));
    }

    private static async Task<IResult> CreateTemplate([FromBody] CreateTemplateRequest req, GameDbContext db,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("AdminEndpoints");

        if (!InputValidator.IsValidTemplateName(req.Name))
            return Results.BadRequest("Invalid template name (alphanumeric, spaces, hyphens only, max 100 chars)");

        if (!InputValidator.IsValidGameType(req.GameType))
            return Results.BadRequest("Invalid game type (alphanumeric only)");

        if (req.MaxPlayers is < 1 or > 100)
            return Results.BadRequest("Max players must be between 1 and 100");

        if (req.EntryFee < 0 || !InputValidator.IsValidCoinAmount(req.EntryFee))
            return Results.BadRequest("Invalid entry fee");

        if (!InputValidator.IsValidConfigJson(req.ConfigJson))
            return Results.BadRequest("Invalid configuration JSON format or exceeds size limit");

        var template = new GameRoomTemplate
        {
            Name = req.Name,
            GameType = req.GameType,
            MaxPlayers = req.MaxPlayers,
            EntryFee = req.EntryFee,
            ConfigJson = req.ConfigJson
        };
        db.RoomTemplates.Add(template);
        await db.SaveChangesAsync();

        logger.LogInformation("Template created: {TemplateId} ({Name})", template.Id,
            InputValidator.SanitizeForLogging(req.Name));
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
        if (req.TemplateId <= 0)
            return Results.BadRequest("Invalid template ID");

        var template = await db.RoomTemplates.FindAsync(req.TemplateId);
        if (template == null) return Results.NotFound("Template not found");

        return await CreateGameInternal(sp, template.GameType, template.MaxPlayers, template.EntryFee,
            template.ConfigJson);
    }

    private static async Task<IResult> CreateAdHocGame(
        [FromBody] CreateGameRequest req,
        IServiceProvider sp)
    {
        if (!InputValidator.IsValidGameType(req.GameType))
            return Results.BadRequest("Invalid game type format");

        if (!InputValidator.IsValidCoinAmount(req.EntryFee) || req.EntryFee < 0)
            return Results.BadRequest("Invalid entry fee");

        if (!InputValidator.IsValidConfigJson(req.ConfigJson))
            return Results.BadRequest("Invalid config JSON");

        return await CreateGameInternal(sp, req.GameType, req.PlayerCount, req.EntryFee, req.ConfigJson);
    }

    private static async Task<IResult> CreateGameInternal(
        IServiceProvider sp,
        string gameType,
        int maxPlayers,
        long entryFee,
        string? configJson)
    {
        if (!InputValidator.IsValidGameType(gameType))
            return Results.BadRequest("Invalid game type");

        if (maxPlayers is < 1 or > 100)
            return Results.BadRequest("Max players must be between 1 and 100");

        var roomService = sp.GetKeyedService<IGameRoomService>(gameType);
        if (roomService == null)
            return Results.BadRequest($"Game type '{InputValidator.SanitizeForLogging(gameType)}' not supported");

        var logger = sp.GetRequiredService<ILogger<IGameRoomService>>();
        var configDict = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(configJson))
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(configJson);
                if (dict != null)
                    foreach (var kvp in dict)
                        configDict[kvp.Key] = kvp.Value?.ToString() ?? "";
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Invalid JSON config provided for game type {GameType}",
                    InputValidator.SanitizeForLogging(gameType));
                return Results.BadRequest("Invalid configuration JSON format");
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
        if (!InputValidator.IsValidRoomId(roomId))
            return Results.BadRequest("Invalid room ID format");

        var gameType = await registry.GetGameTypeAsync(roomId);
        if (gameType == null) return Results.NotFound("Room not found");
        var engine = sp.GetKeyedService<IGameEngine>(gameType);
        if (engine == null) return Results.NotFound("Game engine not available");
        var state = await engine.GetStateAsync(roomId);
        return state != null ? Results.Ok(state) : Results.NotFound();
    }

    private static async Task<IResult> DeleteGame(string roomId, IServiceProvider sp, IRoomRegistry registry)
    {
        if (!InputValidator.IsValidRoomId(roomId))
            return Results.BadRequest("Invalid room ID format");

        var gameType = await registry.GetGameTypeAsync(roomId);
        if (gameType == null) return Results.NotFound("Room not found");
        var roomService = sp.GetKeyedService<IGameRoomService>(gameType);
        if (roomService != null) await roomService.DeleteRoomAsync(roomId);
        return Results.Ok(new { RoomId = roomId, Deleted = true });
    }

    private static async Task<IResult> GetGames(
        IEnumerable<IGameModule> modules,
        IServiceProvider sp,
        IRoomRegistry registry,
        [FromQuery] string? gameType = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        if (!string.IsNullOrEmpty(gameType) && !InputValidator.IsValidGameType(gameType))
            return Results.BadRequest("Invalid game type filter");

        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);

        var allGames = new List<GameRoomDto>();

        var modulesToQuery = string.IsNullOrEmpty(gameType)
            ? modules
            : modules.Where(m => m.GameName.Equals(gameType, StringComparison.OrdinalIgnoreCase));

        foreach (var module in modulesToQuery)
        {
            var engine = sp.GetKeyedService<IGameEngine>(module.GameName);
            if (engine == null) continue;

            var cursor = (long)(page - 1) * pageSize;
            var (roomIds, _) = await registry.GetRoomIdsPagedAsync(module.GameName, cursor, pageSize);

            var states = await engine.GetManyStatesAsync(roomIds.ToList());

            foreach (var state in states)
                allGames.Add(new GameRoomDto(
                    state.RoomId,
                    state.GameType,
                    state.Meta.CurrentPlayerCount,
                    state.Meta.MaxPlayers,
                    state.Meta.IsPublic,
                    state.Meta.PlayerSeats));

            if (allGames.Count >= pageSize) break;
        }

        return Results.Ok(allGames);
    }

    private static async Task<IResult> GetPlayers(
        GameDbContext db, 
        IRoomRegistry registry,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);

        var players = await db.PlayerProfiles
            .AsNoTracking()
            .Where(p => !p.IsDeleted)
            .Include(p => p.User)
            .OrderBy(p => p.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new {
                p.Id,
                p.UserId,
                UserName = p.User.UserName ?? "Unknown",
                Email = p.User.Email ?? "No Email",
                p.Coins,
                p.AvatarId
            })
            .ToListAsync();

        var onlineUserIds = await registry.GetOnlineUserIdsAsync();

        var dtos = players.Select(p => new AdminPlayerDto(
            p.Id, 
            p.UserId, 
            p.UserName, 
            p.Email, 
            p.Coins,
            onlineUserIds.Contains(p.UserId),
            p.AvatarId
        )).ToList();

        return Results.Ok(dtos);
    }

    private static async Task<IResult> GetPlayerHistory(
        string userId,
        GameDbContext db,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        if (!InputValidator.IsValidUserId(userId))
            return Results.BadRequest("Invalid user ID format");

        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);

        var query = db.WalletTransactions
            .AsNoTracking()
            .Where(t => t.UserId == userId);

        var totalCount = await query.CountAsync();

        var items = await query
            .OrderByDescending(t => t.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new WalletTransactionDto(
                t.Id,
                t.Amount,
                t.BalanceAfter,
                t.TransactionType,
                t.Description,
                t.ReferenceId,
                t.CreatedAt))
            .ToListAsync();

        return Results.Ok(new PagedResult<WalletTransactionDto>(items, totalCount, page, pageSize));
    }

    private static async Task<IResult> UpdatePlayerCoins(
        string userId,
        [FromBody] UpdateCoinRequest req,
        HttpContext httpContext,
        GameDbContext db,
        IGameEventPublisher publisher,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("AdminEndpoints");

        if (!InputValidator.IsValidUserId(userId))
            return Results.BadRequest("Invalid user ID format");

        if (!InputValidator.IsValidCoinAmount(req.Amount))
            return Results.BadRequest("Invalid amount");

        var adminId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "api-key-admin";

        var rows = await db.PlayerProfiles.Where(p => p.UserId == userId).ExecuteUpdateAsync(setters =>
            setters.SetProperty(p => p.Coins, p => p.Coins + req.Amount).SetProperty(p => p.Version, Guid.NewGuid()));
        if (rows == 0) return Results.NotFound();

        var profile = await db.PlayerProfiles.Include(p => p.User).AsNoTracking().FirstAsync(p => p.UserId == userId);

        var auditEntry = new WalletTransaction
        {
            UserId = userId,
            Amount = req.Amount,
            BalanceAfter = profile.Coins,
            TransactionType = "AdminAdjust",
            Description = $"Admin adjustment by {InputValidator.SanitizeForLogging(adminId, 50)}",
            ReferenceId = $"ADMIN:{InputValidator.SanitizeForLogging(adminId, 50)}",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.WalletTransactions.Add(auditEntry);
        await db.SaveChangesAsync();

        logger.LogInformation("Admin {AdminId} adjusted coins for user {UserId} by {Amount}. New balance: {Balance}",
            InputValidator.SanitizeForLogging(adminId, 50), userId, req.Amount, profile.Coins);

        await publisher.PublishPlayerUpdatedAsync(new PlayerUpdatedMessage(profile.UserId, profile.Coins,
            profile.User?.UserName, profile.User?.Email));
        return Results.Ok(new { NewBalance = profile.Coins });
    }

    private static async Task<IResult> DeletePlayer(string userId, UserManager<ApplicationUser> userManager,
        GameDbContext db, IGameEventPublisher publisher, ILoggerFactory loggerFactory)
    {
        if (!InputValidator.IsValidUserId(userId))
            return Results.BadRequest("Invalid user ID format");

        var logger = loggerFactory.CreateLogger("AdminEndpoints");
        var user = await userManager.FindByIdAsync(userId);
        if (user == null) return Results.NotFound();

        var rows = await db.PlayerProfiles
            .Where(p => p.UserId == userId && !p.IsDeleted)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(p => p.IsDeleted, true)
                .SetProperty(p => p.DeletedAt, DateTimeOffset.UtcNow));

        if (rows == 0) logger.LogWarning("Attempted to delete already-deleted player {UserId}", userId);

        var result = await userManager.DeleteAsync(user);
        if (!result.Succeeded) return Results.BadRequest(result.Errors);

        logger.LogInformation("Player deleted: {UserId}", userId);
        await publisher.PublishPlayerUpdatedAsync(new PlayerUpdatedMessage(userId, 0, user.UserName, user.Email,
            PlayerChangeType.Deleted));
        return Results.Ok();
    }

    public record CreateGameRequest(string GameType, int PlayerCount, long EntryFee = 0, string? ConfigJson = null);
    public record UpdateSettingRequest(string Key, string Value, string? Description);
}