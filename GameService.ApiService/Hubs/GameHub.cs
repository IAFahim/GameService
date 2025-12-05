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

namespace GameService.ApiService.Hubs;

/// <summary>
///     Unified game hub - handles ALL game types through a single SignalR endpoint.
///     Clients connect once and can interact with any game type.
///     Includes chat functionality and reconnection grace period handling.
///     All state is stored in Redis for horizontal scaling.
/// </summary>
[Authorize]
public class GameHub(
    IRoomRegistry roomRegistry,
    IServiceProvider serviceProvider,
    IOptions<GameServiceOptions> options,
    ILogger<GameHub> logger) : Hub
{
    private readonly int _reconnectionGracePeriodSeconds = options.Value.Session.ReconnectionGracePeriodSeconds;
    private readonly int _maxMessagesPerMinute = options.Value.RateLimit.SignalRMessagesPerMinute;
    private readonly int _maxConnectionsPerUser = options.Value.Session.MaxConnectionsPerUser;

    private string UserId => Context.User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
    private string UserName => Context.User?.Identity?.Name ?? "Unknown";

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
        // Check connection limit per user
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

        // Check if player was disconnected and is reconnecting within grace period
        var disconnectedRoomId = await roomRegistry.TryGetAndRemoveDisconnectedPlayerAsync(UserId);
        if (disconnectedRoomId != null)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, disconnectedRoomId);
            await Clients.Group(disconnectedRoomId)
                .SendAsync("PlayerReconnected", new PlayerReconnectedEvent(UserId, UserName));
            logger.LogInformation("Player {UserId} reconnected to room {RoomId} within grace period", 
                UserId, disconnectedRoomId);
        }

        logger.LogDebug("Player {UserId} connected to GameHub (connection {Count})", UserId, connectionCount);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await roomRegistry.DecrementConnectionCountAsync(UserId);

        // O(1) lookup instead of iterating all rooms
        var roomId = await roomRegistry.GetUserRoomAsync(UserId);
        
        if (roomId != null)
        {
            var gameType = await roomRegistry.GetGameTypeAsync(roomId);
            if (gameType != null)
            {
                // Store in Redis with TTL for automatic cleanup
                await roomRegistry.SetDisconnectedPlayerAsync(
                    UserId, 
                    roomId, 
                    TimeSpan.FromSeconds(_reconnectionGracePeriodSeconds + 1));

                await Clients.Group(roomId).SendAsync("PlayerDisconnected",
                    new PlayerDisconnectedEvent(UserId, UserName, _reconnectionGracePeriodSeconds));

                // Schedule cleanup after grace period using fire-and-forget
                // This works across instances because Redis TTL handles the state
                var capturedUserId = UserId;
                var capturedUserName = UserName;
                var capturedRoomId = roomId;
                var capturedGameType = gameType;
                var gracePeriod = _reconnectionGracePeriodSeconds;
                
                _ = Task.Delay(TimeSpan.FromSeconds(gracePeriod + 2)).ContinueWith(async _ =>
                {
                    // Check if player is still disconnected (Redis key still exists = reconnected)
                    var stillDisconnected = await roomRegistry.TryGetAndRemoveDisconnectedPlayerAsync(capturedUserId);
                    if (stillDisconnected == capturedRoomId)
                    {
                        var roomService = serviceProvider.GetKeyedService<IGameRoomService>(capturedGameType);
                        if (roomService != null) 
                            await roomService.LeaveRoomAsync(capturedRoomId, capturedUserId);

                        await roomRegistry.RemoveUserRoomAsync(capturedUserId);

                        var hubContext = serviceProvider.GetRequiredService<IHubContext<GameHub>>();
                        await hubContext.Clients.Group(capturedRoomId).SendAsync("PlayerLeft",
                            new PlayerLeftEvent(capturedUserId, capturedUserName));

                        logger.LogInformation("Player {UserId} grace period expired, removed from room {RoomId}",
                            capturedUserId, capturedRoomId);
                    }
                });
            }
        }

        logger.LogDebug("Player {UserId} disconnected from GameHub", UserId);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    ///     Send a chat message to all players in a room
    /// </summary>
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

        var chatEvent = new ChatMessageEvent(UserId, UserName, message, DateTimeOffset.UtcNow);
        await Clients.Group(roomId).SendAsync("ChatMessage", chatEvent);

        logger.LogDebug("Chat in {RoomId}: {UserName}: {Message}", roomId, UserName, message);
    }

    /// <summary>
    ///     Create a new game room based on a predefined Template (Room Type).
    /// </summary>
    /// <param name="templateName">The unique name of the room template (e.g., "StandardLudo", "99Mines")</param>
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

            // Track user→room mapping for creator
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

    /// <summary>
    ///     Join an existing game room using its Short ID.
    /// </summary>
    public async Task<JoinRoomResponse> JoinRoom(string roomId)
    {
        var gameType = await roomRegistry.GetGameTypeAsync(roomId);
        if (gameType == null) return new JoinRoomResponse(false, -1, "Room not found");

        var roomService = serviceProvider.GetKeyedService<IGameRoomService>(gameType);
        if (roomService == null) return new JoinRoomResponse(false, -1, "Game type not supported");

        // Check room meta for entry fee before joining
        var meta = await roomService.GetRoomMetaAsync(roomId);
        if (meta == null) return new JoinRoomResponse(false, -1, "Room not found");
        
        // Deduct entry fee if required (and player not already in room)
        if (meta.EntryFee > 0 && !meta.PlayerSeats.ContainsKey(UserId))
        {
            using var scope = serviceProvider.CreateScope();
            var economyService = scope.ServiceProvider.GetRequiredService<IEconomyService>();
            var feeResult = await economyService.DeductEntryFeeAsync(UserId, meta.EntryFee, roomId);
            if (!feeResult.Success)
            {
                return new JoinRoomResponse(false, -1, feeResult.ErrorMessage ?? "Insufficient funds for entry fee");
            }
            logger.LogInformation("Player {UserId} paid entry fee {Fee} for room {RoomId}", UserId, meta.EntryFee, roomId);
        }

        var result = await roomService.JoinRoomAsync(roomId, UserId);
        if (!result.Success) return new JoinRoomResponse(false, -1, result.ErrorMessage);

        // Track user→room mapping for O(1) disconnect lookup
        await roomRegistry.SetUserRoomAsync(UserId, roomId);

        await Groups.AddToGroupAsync(Context.ConnectionId, roomId);

        await Clients.Group(roomId)
            .SendAsync("PlayerJoined", new PlayerJoinedEvent(UserId, UserName, result.SeatIndex));

        var engine = serviceProvider.GetKeyedService<IGameEngine>(gameType);
        if (engine != null)
        {
            var state = await engine.GetStateAsync(roomId);
            if (state != null) await Clients.Caller.SendAsync("GameState", state);
        }

        logger.LogInformation("Player {UserId} joined room {RoomId} (Seat {Seat})", UserId, roomId, result.SeatIndex);
        return new JoinRoomResponse(true, result.SeatIndex, null);
    }

    /// <summary>
    ///     Leave a game room
    /// </summary>
    public async Task LeaveRoom(string roomId)
    {
        var gameType = await roomRegistry.GetGameTypeAsync(roomId);
        if (gameType != null)
        {
            var roomService = serviceProvider.GetKeyedService<IGameRoomService>(gameType);
            if (roomService != null) await roomService.LeaveRoomAsync(roomId, UserId);
        }

        // Remove user→room mapping
        await roomRegistry.RemoveUserRoomAsync(UserId);

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId);
        await Clients.Group(roomId).SendAsync("PlayerLeft", new PlayerLeftEvent(UserId, UserName));

        logger.LogInformation("Player {UserId} left room {RoomId}", UserId, roomId);
    }

    /// <summary>
    ///     Execute a game action (Roll, Move, Bet, Draw, etc.)
    ///     This is the universal action handler for ALL game types.
    /// </summary>
    public async Task<GameActionResult> PerformAction(string roomId, string actionName, JsonElement payload)
    {
        // Rate limit check using Redis
        if (!await CheckRateLimitAsync())
            return GameActionResult.Error("Rate limit exceeded. Please slow down.");

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
                await Clients.Group(roomId).SendAsync("GameState", result.NewState);

            foreach (var evt in result.Events) await Clients.Group(roomId).SendAsync(evt.EventName, evt.Data);

            if (!result.Success)
                await Clients.Caller.SendAsync("ActionError",
                    new ActionErrorEvent(actionName, result.ErrorMessage ?? "Unknown error"));

            // Update room activity for timeout tracking
            if (result.Success)
                await roomRegistry.UpdateRoomActivityAsync(roomId, gameType);

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing action {Action} in room {RoomId}", actionName, roomId);
            return GameActionResult.Error("An error occurred while processing your action");
        }
        finally
        {
            await roomRegistry.ReleaseLockAsync(roomId);
        }
    }

    /// <summary>
    ///     Get current game state manually
    /// </summary>
    public async Task<GameStateResponse?> GetState(string roomId)
    {
        var gameType = await roomRegistry.GetGameTypeAsync(roomId);
        if (gameType == null) return null;

        var engine = serviceProvider.GetKeyedService<IGameEngine>(gameType);
        return engine != null ? await engine.GetStateAsync(roomId) : null;
    }

    /// <summary>
    ///     Get legal actions for current player (User Assistance)
    /// </summary>
    public async Task<IReadOnlyList<string>> GetLegalActions(string roomId)
    {
        var gameType = await roomRegistry.GetGameTypeAsync(roomId);
        if (gameType == null) return [];

        var engine = serviceProvider.GetKeyedService<IGameEngine>(gameType);
        return engine != null ? await engine.GetLegalActionsAsync(roomId, UserId) : [];
    }
}

public sealed record CreateRoomResponse(bool Success, string? RoomId, string? ErrorMessage);

public sealed record JoinRoomResponse(bool Success, int SeatIndex, string? ErrorMessage);

public sealed record PlayerJoinedEvent(string UserId, string UserName, int SeatIndex);

public sealed record PlayerLeftEvent(string UserId, string UserName);

public sealed record ActionErrorEvent(string Action, string ErrorMessage);

public sealed record PlayerDisconnectedEvent(string UserId, string UserName, int GracePeriodSeconds);

public sealed record PlayerReconnectedEvent(string UserId, string UserName);

public sealed record ChatMessageEvent(string UserId, string UserName, string Message, DateTimeOffset Timestamp);