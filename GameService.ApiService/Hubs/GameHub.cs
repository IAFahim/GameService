using System.Security.Claims;
using System.Text.Json;
using GameService.ApiService.Features.Economy;
using GameService.GameCore;
using GameService.ServiceDefaults.Configuration;
using GameService.ServiceDefaults.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace GameService.ApiService.Hubs;


[Authorize]
public class GameHub(
    IRoomRegistry roomRegistry,
    IServiceProvider serviceProvider,
    IOptions<GameServiceOptions> options,
    IConnectionMultiplexer redis,
    ILogger<GameHub> logger) : Hub<IGameClient>
{
    private readonly IDatabase _redisDb = redis.GetDatabase();
    private readonly int _maxConnectionsPerUser = options.Value.Session.MaxConnectionsPerUser;
    private readonly int _maxMessagesPerMinute = options.Value.RateLimit.SignalRMessagesPerMinute;
    private readonly int _reconnectionGracePeriodSeconds = options.Value.Session.ReconnectionGracePeriodSeconds;

    private string UserId => Context.User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
    private string UserName => Context.User?.Identity?.Name ?? "Unknown";

    private async Task<bool> IsCommandProcessedAsync(string roomId, string commandId)
    {
        if (string.IsNullOrEmpty(commandId)) return false;
        var key = $"cmd:{roomId}:{commandId}";
        return await _redisDb.KeyExistsAsync(key);
    }
    
    private async Task MarkCommandProcessedAsync(string roomId, string commandId)
    {
        if (string.IsNullOrEmpty(commandId)) return;
        var key = $"cmd:{roomId}:{commandId}";
        await _redisDb.StringSetAsync(key, "1", TimeSpan.FromMinutes(5));
    }

    private async Task<bool> CheckRateLimitAsync()
    {
        if (!await roomRegistry.CheckRateLimitAsync(UserId, _maxMessagesPerMinute))
        {
            logger.LogWarning("Rate limit exceeded for user {UserId}", UserId);
            return false;
        }

        return true;
    }

    public override async Task OnConnectedAsync()
    {
        var connectionCount = await roomRegistry.IncrementConnectionCountAsync(UserId);
        if (connectionCount > _maxConnectionsPerUser)
        {
            await roomRegistry.DecrementConnectionCountAsync(UserId);
            logger.LogWarning("Connection limit exceeded for user {UserId}: {Count}/{Max}",
                UserId, connectionCount, _maxConnectionsPerUser);
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, "Lobby");

        var disconnectedRoomId = await roomRegistry.TryGetAndRemoveDisconnectedPlayerAsync(UserId);
        if (disconnectedRoomId != null)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, disconnectedRoomId);
            await Clients.Group(disconnectedRoomId)
                .PlayerReconnected(new PlayerReconnectedPayload(UserId, UserName));
            logger.LogInformation("Player {UserId} reconnected to room {RoomId} within grace period",
                UserId, disconnectedRoomId);
        }

        logger.LogDebug("Player {UserId} connected to GameHub (connection {Count})", UserId, connectionCount);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await roomRegistry.DecrementConnectionCountAsync(UserId);

        var roomId = await roomRegistry.GetUserRoomAsync(UserId);

        if (roomId != null)
        {
            var gameType = await roomRegistry.GetGameTypeAsync(roomId);
            if (gameType != null)
            {
                var lockKey = $"disconnect:{UserId}";
                if (await roomRegistry.TryAcquireLockAsync(lockKey,
                        TimeSpan.FromSeconds(_reconnectionGracePeriodSeconds + 5)))
                    try
                    {
                        await roomRegistry.SetDisconnectedPlayerAsync(
                            UserId,
                            roomId,
                            TimeSpan.FromSeconds(_reconnectionGracePeriodSeconds + 1));

                        await Clients.Group(roomId).PlayerDisconnected(
                            new PlayerDisconnectedPayload(UserId, UserName, _reconnectionGracePeriodSeconds));

                        var capturedUserId = UserId;
                        var capturedUserName = UserName;
                        var capturedRoomId = roomId;
                        var capturedGameType = gameType;
                        var gracePeriod = _reconnectionGracePeriodSeconds;
                        var capturedLockKey = lockKey;

                        _ = Task.Delay(TimeSpan.FromSeconds(gracePeriod + 2)).ContinueWith(async _ =>
                        {
                            if (!await roomRegistry.TryAcquireLockAsync(capturedLockKey, TimeSpan.FromSeconds(5)))
                                return;

                            try
                            {
                                var stillDisconnected =
                                    await roomRegistry.TryGetAndRemoveDisconnectedPlayerAsync(capturedUserId);
                                if (stillDisconnected == capturedRoomId)
                                {
                                    var roomService =
                                        serviceProvider.GetKeyedService<IGameRoomService>(capturedGameType);
                                    if (roomService != null)
                                        await roomService.LeaveRoomAsync(capturedRoomId, capturedUserId);

                                    await roomRegistry.RemoveUserRoomAsync(capturedUserId);

                                    var hubContext = serviceProvider.GetRequiredService<IHubContext<GameHub, IGameClient>>();
                                    await hubContext.Clients.Group(capturedRoomId).PlayerLeft(
                                        new PlayerLeftPayload(capturedUserId, capturedUserName));

                                    logger.LogInformation(
                                        "Player {UserId} grace period expired, removed from room {RoomId}",
                                        capturedUserId, capturedRoomId);
                                }
                            }
                            finally
                            {
                                await roomRegistry.ReleaseLockAsync(capturedLockKey);
                            }
                        });
                    }
                    finally
                    {
                        await roomRegistry.ReleaseLockAsync(lockKey);
                    }
            }
        }

        logger.LogDebug("Player {UserId} disconnected from GameHub", UserId);
        await base.OnDisconnectedAsync(exception);
    }

    
    public async Task SendChatMessage(string roomId, string message)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > 500)
            return;

        var gameType = await roomRegistry.GetGameTypeAsync(roomId);
        if (gameType == null) return;

        var engine = serviceProvider.GetKeyedService<IGameEngine>(gameType);
        if (engine == null) return;

        var state = await engine.GetStateAsync(roomId);
        if (state?.Meta.PlayerSeats.ContainsKey(UserId) != true)
            return;

        var chatEvent = new ChatMessagePayload(UserId, UserName, message, DateTimeOffset.UtcNow);
        await Clients.Group(roomId).ChatMessage(chatEvent);

        logger.LogDebug("Chat in {RoomId}: {UserName}: {Message}", roomId, UserName, message);
    }

    public async Task<CreateRoomResponse> CreateRoom(string templateName)
    {
        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();

        var template = await db.RoomTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Name == templateName);

        if (template == null) return new CreateRoomResponse(false, null, $"Room type '{templateName}' not found.");

        return await CreateRoomInternal(template.GameType, template.MaxPlayers, template.EntryFee, template.ConfigJson);
    }

    private async Task<CreateRoomResponse> CreateRoomInternal(string gameType, int maxPlayers, long entryFee,
        string? configJson)
    {
        if (string.IsNullOrWhiteSpace(gameType))
            return new CreateRoomResponse(false, null, "Game type is required.");

        if (maxPlayers < 1 || maxPlayers > 100)
            return new CreateRoomResponse(false, null, "Max players must be between 1 and 100.");

        var roomService = serviceProvider.GetKeyedService<IGameRoomService>(gameType);
        if (roomService == null)
            return new CreateRoomResponse(false, null, $"Game type '{gameType}' is not supported.");

        try
        {
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
                    logger.LogWarning(ex, "Invalid JSON config for game type {GameType}", gameType);
                    return new CreateRoomResponse(false, null, "Invalid configuration JSON format.");
                }

            var meta = new GameRoomMeta
            {
                GameType = gameType,
                MaxPlayers = maxPlayers,
                EntryFee = entryFee,
                Config = configDict,
                IsPublic = true,
                PlayerSeats = new Dictionary<string, int> { [UserId] = 0 }
            };

            var roomId = await roomService.CreateRoomAsync(meta);

            await roomRegistry.SetUserRoomAsync(UserId, roomId);

            await Groups.AddToGroupAsync(Context.ConnectionId, roomId);

            logger.LogInformation("Room {RoomId} created (Type: {GameType}, MaxPlayers: {MaxPlayers}) by {UserId}",
                roomId, gameType, maxPlayers, UserId);

            return new CreateRoomResponse(true, roomId, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create room for game type {GameType}", gameType);
            return new CreateRoomResponse(false, null, "An unexpected error occurred while creating the room.");
        }
    }

    public async Task<JoinRoomResponse> JoinRoom(string roomId)
    {
        var gameType = await roomRegistry.GetGameTypeAsync(roomId);
        if (gameType == null) return new JoinRoomResponse(false, -1, "Room not found");

        var roomService = serviceProvider.GetKeyedService<IGameRoomService>(gameType);
        if (roomService == null) return new JoinRoomResponse(false, -1, "Game type not supported");

        var meta = await roomService.GetRoomMetaAsync(roomId);
        if (meta == null) return new JoinRoomResponse(false, -1, "Room not found");

        if (!meta.IsPublic && !meta.PlayerSeats.ContainsKey(UserId))
        {
            logger.LogWarning("Blocked unauthorized join attempt to private room {RoomId} by {UserId}", roomId, UserId);
            return new JoinRoomResponse(false, -1, "This is a private room. You need an invite to join.");
        }

        EntryFeeReservation? reservation = null;
        if (meta.EntryFee > 0 && !meta.PlayerSeats.ContainsKey(UserId))
        {
            using var scope = serviceProvider.CreateScope();
            var economyService = scope.ServiceProvider.GetRequiredService<IEconomyService>();

            reservation = await economyService.ReserveEntryFeeAsync(UserId, meta.EntryFee, roomId);
            if (!reservation.Success)
                return new JoinRoomResponse(false, -1, reservation.ErrorMessage ?? "Insufficient funds for entry fee");
        }

        var result = await roomService.JoinRoomAsync(roomId, UserId);

        if (!result.Success)
        {
            if (reservation != null)
            {
                using var scope = serviceProvider.CreateScope();
                var economyService = scope.ServiceProvider.GetRequiredService<IEconomyService>();
                await economyService.RefundEntryFeeAsync(reservation);
                logger.LogInformation("Refunded entry fee for player {UserId} - failed to join room {RoomId}: {Error}",
                    UserId, roomId, result.ErrorMessage);
            }

            return new JoinRoomResponse(false, -1, result.ErrorMessage);
        }

        if (reservation != null)
        {
            using var scope = serviceProvider.CreateScope();
            var economyService = scope.ServiceProvider.GetRequiredService<IEconomyService>();
            await economyService.CommitEntryFeeAsync(reservation);
            logger.LogInformation("Player {UserId} paid entry fee {Fee} for room {RoomId}", UserId, meta.EntryFee,
                roomId);
        }

        await roomRegistry.SetUserRoomAsync(UserId, roomId);

        await Groups.AddToGroupAsync(Context.ConnectionId, roomId);

        await Clients.Group(roomId)
            .PlayerJoined(new PlayerJoinedPayload(UserId, UserName, result.SeatIndex));

        var engine = serviceProvider.GetKeyedService<IGameEngine>(gameType);
        if (engine != null)
        {
            var state = await engine.GetStateAsync(roomId);
            if (state != null) await Clients.Caller.GameState(state);
        }

        logger.LogInformation("Player {UserId} joined room {RoomId} (Seat {Seat})", UserId, roomId, result.SeatIndex);
        return new JoinRoomResponse(true, result.SeatIndex, null);
    }

    public async Task LeaveRoom(string roomId)
    {
        var gameType = await roomRegistry.GetGameTypeAsync(roomId);
        if (gameType != null)
        {
            var roomService = serviceProvider.GetKeyedService<IGameRoomService>(gameType);
            if (roomService != null) await roomService.LeaveRoomAsync(roomId, UserId);
        }

        await roomRegistry.RemoveUserRoomAsync(UserId);

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId);
        await Clients.Group(roomId).PlayerLeft(new PlayerLeftPayload(UserId, UserName));

        logger.LogInformation("Player {UserId} left room {RoomId}", UserId, roomId);
    }

    public async Task<GameActionResult> PerformAction(string roomId, string actionName, JsonElement payload, string? commandId = null)
    {
        if (!await CheckRateLimitAsync())
            return GameActionResult.Error("Rate limit exceeded. Please slow down.");

        if (!string.IsNullOrEmpty(commandId) && await IsCommandProcessedAsync(roomId, commandId))
        {
            logger.LogDebug("Duplicate command {CommandId} for room {RoomId} ignored", commandId, roomId);
            return GameActionResult.Error("Command already processed");
        }

        var gameType = await roomRegistry.GetGameTypeAsync(roomId);
        if (gameType == null) return GameActionResult.Error("Room not found");

        var engine = serviceProvider.GetKeyedService<IGameEngine>(gameType);
        if (engine == null) return GameActionResult.Error("Game engine not available");

        if (!await roomRegistry.TryAcquireLockAsync(roomId, TimeSpan.FromSeconds(2)))
            return GameActionResult.Error("Game is busy. Please retry.");

        try
        {
            var command = new GameCommand(UserId, actionName, payload);
            var result = await engine.ExecuteAsync(roomId, command);

            if (result.Success && result.ShouldBroadcast && result.NewState != null)
                await Clients.Group(roomId).GameState((GameStateResponse)result.NewState);

            foreach (var evt in result.Events)
                await Clients.Group(roomId).GameEvent(new GameEventPayload(evt.EventName, evt.Data, evt.Timestamp));

            if (!result.Success)
                await Clients.Caller.ActionError(
                    new ActionErrorPayload(actionName, result.ErrorMessage ?? "Unknown error"));

            if (result.Success)
            {
                await roomRegistry.UpdateRoomActivityAsync(roomId, gameType);
                await MarkCommandProcessedAsync(roomId, commandId ?? "");
            }

            if (result.GameEnded != null)
                try
                {
                    using var scope = serviceProvider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
                    var info = result.GameEnded;

                    var outboxPayload = JsonSerializer.Serialize(new GameEndedPayload(
                        info.RoomId,
                        info.GameType,
                        info.PlayerSeats,
                        info.WinnerUserId,
                        info.TotalPot,
                        info.StartedAt,
                        info.WinnerRanking,
                        result.NewState));

                    db.OutboxMessages.Add(new OutboxMessage
                    {
                        EventType = "GameEnded",
                        Payload = outboxPayload,
                        CreatedAt = DateTimeOffset.UtcNow
                    });

                    await db.SaveChangesAsync();
                    logger.LogInformation("Scheduled archival for Room {RoomId}", info.RoomId);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to schedule game archival for {RoomId}", roomId);
                }

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing action {Action} in room {RoomId}", actionName, roomId);
            return GameActionResult.Error($"An error occurred: {ex.Message}");
        }
        finally
        {
            await roomRegistry.ReleaseLockAsync(roomId);
        }
    }

    public async Task<GameStateResponse?> GetState(string roomId)
    {
        var gameType = await roomRegistry.GetGameTypeAsync(roomId);
        if (gameType == null) return null;

        var engine = serviceProvider.GetKeyedService<IGameEngine>(gameType);
        return engine != null ? await engine.GetStateAsync(roomId) : null;
    }

    public async Task<IReadOnlyList<string>> GetLegalActions(string roomId)
    {
        var gameType = await roomRegistry.GetGameTypeAsync(roomId);
        if (gameType == null) return [];

        var engine = serviceProvider.GetKeyedService<IGameEngine>(gameType);
        return engine != null ? await engine.GetLegalActionsAsync(roomId, UserId) : [];
    }

    public async Task<SpectateRoomResponse> SpectateRoom(string roomId)
    {
        var gameType = await roomRegistry.GetGameTypeAsync(roomId);
        if (gameType == null) return new SpectateRoomResponse(false, "Room not found");

        var engine = serviceProvider.GetKeyedService<IGameEngine>(gameType);
        if (engine == null) return new SpectateRoomResponse(false, "Game engine not available");

        var spectatorGroup = $"{roomId}:spectators";
        await Groups.AddToGroupAsync(Context.ConnectionId, roomId);
        await Groups.AddToGroupAsync(Context.ConnectionId, spectatorGroup);

        await _redisDb.SetAddAsync($"spectators:{roomId}", UserId);

        var state = await engine.GetStateAsync(roomId);
        if (state != null) await Clients.Caller.GameState(state);

        logger.LogInformation("Player {UserId} started spectating room {RoomId}", UserId, roomId);
        return new SpectateRoomResponse(true, null);
    }

    public async Task StopSpectating(string roomId)
    {
        var spectatorGroup = $"{roomId}:spectators";
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, spectatorGroup);

        await _redisDb.SetRemoveAsync($"spectators:{roomId}", UserId);

        logger.LogInformation("Player {UserId} stopped spectating room {RoomId}", UserId, roomId);
    }

    public async Task<long> GetSpectatorCount(string roomId)
    {
        return await _redisDb.SetLengthAsync($"spectators:{roomId}");
    }
}

public sealed record CreateRoomResponse(bool Success, string? RoomId, string? ErrorMessage);

public sealed record JoinRoomResponse(bool Success, int SeatIndex, string? ErrorMessage);

public sealed record SpectateRoomResponse(bool Success, string? ErrorMessage);

public sealed record PlayerJoinedEvent(string UserId, string UserName, int SeatIndex);

public sealed record PlayerLeftEvent(string UserId, string UserName);

public sealed record ActionErrorEvent(string Action, string ErrorMessage);

public sealed record PlayerDisconnectedEvent(string UserId, string UserName, int GracePeriodSeconds);

public sealed record PlayerReconnectedEvent(string UserId, string UserName);

public sealed record ChatMessageEvent(string UserId, string UserName, string Message, DateTimeOffset Timestamp);

public sealed record GameEndedPayload(
    string RoomId,
    string GameType,
    IReadOnlyDictionary<string, int> PlayerSeats,
    string? WinnerUserId,
    long TotalPot,
    DateTimeOffset StartedAt,
    IReadOnlyList<string>? WinnerRanking,
    object? FinalState);