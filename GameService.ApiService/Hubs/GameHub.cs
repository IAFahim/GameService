using System.Security.Claims;
using System.Text.Json;
using GameService.GameCore;
using GameService.ServiceDefaults.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GameService.ApiService.Hubs;

/// <summary>
/// Unified game hub - handles ALL game types through a single SignalR endpoint.
/// Clients connect once and can interact with any game type.
/// </summary>
[Authorize]
public class GameHub(
    IRoomRegistry roomRegistry,
    IServiceProvider serviceProvider,
    ILogger<GameHub> logger) : Hub
{
    private string UserId => Context.User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
    private string UserName => Context.User?.Identity?.Name ?? "Unknown";

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "Lobby");
        logger.LogInformation("Player {UserId} connected to GameHub", UserId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        logger.LogInformation("Player {UserId} disconnected from GameHub", UserId);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Create a new game room based on a predefined Template (Room Type).
    /// </summary>
    /// <param name="templateName">The unique name of the room template (e.g., "StandardLudo", "99Mines")</param>
    public async Task<CreateRoomResponse> CreateRoom(string templateName)
    {
        // 1. Resolve Database to fetch Template Configuration
        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();

        var template = await db.RoomTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Name == templateName);

        if (template == null)
        {
            return new CreateRoomResponse(false, null, $"Room type '{templateName}' not found.");
        }

        // 2. Resolve the specific Game Service (Ludo, LuckyMine, etc.)
        var roomService = serviceProvider.GetKeyedService<IGameRoomService>(template.GameType);
        if (roomService == null)
        {
            return new CreateRoomResponse(false, null, $"System error: Game type '{template.GameType}' is not registered.");
        }

        try
        {
            // 3. Prepare Configuration from Template
            // This maps the SQL Template entity to the internal GameRoomMeta
            var configDict = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(template.ConfigJson))
            {
                try 
                {
                    var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(template.ConfigJson);
                    if (dict != null)
                    {
                        foreach (var kvp in dict) 
                            configDict[kvp.Key] = kvp.Value.ToString() ?? "";
                    }
                } 
                catch (JsonException) 
                { 
                    logger.LogWarning("Invalid JSON config for template {Template}", templateName);
                }
            }

            var meta = new GameRoomMeta
            {
                GameType = template.GameType,
                MaxPlayers = template.MaxPlayers,
                EntryFee = template.EntryFee,
                Config = configDict,
                IsPublic = true,
                // The creator is automatically added to seat 0 (if logic permits)
                PlayerSeats = new Dictionary<string, int> { [UserId] = 0 } 
            };

            // 4. Create the Room
            // The service will generate the short ID and apply specific rules (like Mine count) based on 'meta'
            var roomId = await roomService.CreateRoomAsync(meta);
            
            // 5. SignalR Setup
            await Groups.AddToGroupAsync(Context.ConnectionId, roomId);
            
            logger.LogInformation("Room {RoomId} created using template {Template} by {UserId}", roomId, templateName, UserId);
            
            return new CreateRoomResponse(true, roomId, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create room with template {Template}", templateName);
            return new CreateRoomResponse(false, null, "An unexpected error occurred while creating the room.");
        }
    }

    /// <summary>
    /// Join an existing game room using its Short ID.
    /// </summary>
    public async Task<JoinRoomResponse> JoinRoom(string roomId)
    {
        // 1. Find which game type this room belongs to (O(1) Redis lookup)
        var gameType = await roomRegistry.GetGameTypeAsync(roomId);
        if (gameType == null)
        {
            return new JoinRoomResponse(false, -1, "Room not found");
        }

        // 2. Get the correct service
        var roomService = serviceProvider.GetKeyedService<IGameRoomService>(gameType);
        if (roomService == null)
        {
            return new JoinRoomResponse(false, -1, "Game type not supported");
        }

        // 3. Attempt to join via the Game Service (handles seats, locking, persistence)
        var result = await roomService.JoinRoomAsync(roomId, UserId);
        if (!result.Success)
        {
            return new JoinRoomResponse(false, -1, result.ErrorMessage);
        }

        // 4. SignalR Setup & Broadcasting
        await Groups.AddToGroupAsync(Context.ConnectionId, roomId);
        
        // Notify others
        await Clients.Group(roomId).SendAsync("PlayerJoined", new PlayerJoinedEvent(UserId, UserName, result.SeatIndex));

        // 5. Send current Game State to the joining player immediately
        var engine = serviceProvider.GetKeyedService<IGameEngine>(gameType);
        if (engine != null)
        {
            var state = await engine.GetStateAsync(roomId);
            if (state != null)
            {
                await Clients.Caller.SendAsync("GameState", state);
            }
        }

        logger.LogInformation("Player {UserId} joined room {RoomId} (Seat {Seat})", UserId, roomId, result.SeatIndex);
        return new JoinRoomResponse(true, result.SeatIndex, null);
    }

    /// <summary>
    /// Leave a game room
    /// </summary>
    public async Task LeaveRoom(string roomId)
    {
        var gameType = await roomRegistry.GetGameTypeAsync(roomId);
        if (gameType != null)
        {
            var roomService = serviceProvider.GetKeyedService<IGameRoomService>(gameType);
            if (roomService != null)
            {
                await roomService.LeaveRoomAsync(roomId, UserId);
            }
        }

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId);
        await Clients.Group(roomId).SendAsync("PlayerLeft", new PlayerLeftEvent(UserId, UserName));
        
        logger.LogInformation("Player {UserId} left room {RoomId}", UserId, roomId);
    }

    /// <summary>
    /// Execute a game action (Roll, Move, Bet, Draw, etc.)
    /// This is the universal action handler for ALL game types.
    /// </summary>
    public async Task<GameActionResult> PerformAction(string roomId, string actionName, JsonElement payload)
    {
        var gameType = await roomRegistry.GetGameTypeAsync(roomId);
        if (gameType == null)
        {
            return GameActionResult.Error("Room not found");
        }

        var engine = serviceProvider.GetKeyedService<IGameEngine>(gameType);
        if (engine == null)
        {
            return GameActionResult.Error("Game engine not available");
        }

        // Distributed Lock to prevent race conditions on game state
        if (!await roomRegistry.TryAcquireLockAsync(roomId, TimeSpan.FromSeconds(2)))
        {
            return GameActionResult.Error("Game is busy. Please retry.");
        }

        try
        {
            var command = new GameCommand(UserId, actionName, payload);
            var result = await engine.ExecuteAsync(roomId, command);

            // Broadcast State Update if the state changed
            if (result.Success && result.ShouldBroadcast && result.NewState != null)
            {
                await Clients.Group(roomId).SendAsync("GameState", result.NewState);
            }

            // Broadcast specific events (e.g., "DiceRolled", "PlayerEliminated")
            foreach (var evt in result.Events)
            {
                await Clients.Group(roomId).SendAsync(evt.EventName, evt.Data);
            }

            // If action failed, notify only the caller
            if (!result.Success)
            {
                await Clients.Caller.SendAsync("ActionError", new ActionErrorEvent(actionName, result.ErrorMessage ?? "Unknown error"));
            }

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
    /// Get current game state manually
    /// </summary>
    public async Task<GameStateResponse?> GetState(string roomId)
    {
        var gameType = await roomRegistry.GetGameTypeAsync(roomId);
        if (gameType == null) return null;

        var engine = serviceProvider.GetKeyedService<IGameEngine>(gameType);
        return engine != null ? await engine.GetStateAsync(roomId) : null;
    }

    /// <summary>
    /// Get legal actions for current player (User Assistance)
    /// </summary>
    public async Task<IReadOnlyList<string>> GetLegalActions(string roomId)
    {
        var gameType = await roomRegistry.GetGameTypeAsync(roomId);
        if (gameType == null) return [];

        var engine = serviceProvider.GetKeyedService<IGameEngine>(gameType);
        return engine != null ? await engine.GetLegalActionsAsync(roomId, UserId) : [];
    }
}

// SignalR DTOs
public sealed record CreateRoomResponse(bool Success, string? RoomId, string? ErrorMessage);
public sealed record JoinRoomResponse(bool Success, int SeatIndex, string? ErrorMessage);
public sealed record PlayerJoinedEvent(string UserId, string UserName, int SeatIndex);
public sealed record PlayerLeftEvent(string UserId, string UserName);
public sealed record ActionErrorEvent(string Action, string ErrorMessage);