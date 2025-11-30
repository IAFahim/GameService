using System.Security.Claims;
using System.Text.Json;
using GameService.GameCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

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
    /// Create a new game room
    /// </summary>
    public async Task<CreateRoomResponse> CreateRoom(string gameType, int playerCount = 4)
    {
        var roomService = serviceProvider.GetKeyedService<IGameRoomService>(gameType);
        if (roomService == null)
        {
            return new CreateRoomResponse(false, null, $"Game type '{gameType}' not supported");
        }

        try
        {
            var roomId = await roomService.CreateRoomAsync(UserId, playerCount);
            await Groups.AddToGroupAsync(Context.ConnectionId, roomId);
            
            logger.LogInformation("Room {RoomId} created for game {GameType} by {UserId}", roomId, gameType, UserId);
            
            return new CreateRoomResponse(true, roomId, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create room for game {GameType}", gameType);
            return new CreateRoomResponse(false, null, "Failed to create room");
        }
    }

    /// <summary>
    /// Join an existing game room
    /// </summary>
    public async Task<JoinRoomResponse> JoinRoom(string roomId)
    {
        var gameType = await roomRegistry.GetGameTypeAsync(roomId);
        if (gameType == null)
        {
            return new JoinRoomResponse(false, -1, "Room not found");
        }

        var roomService = serviceProvider.GetKeyedService<IGameRoomService>(gameType);
        if (roomService == null)
        {
            return new JoinRoomResponse(false, -1, "Game type not supported");
        }

        var result = await roomService.JoinRoomAsync(roomId, UserId);
        if (!result.Success)
        {
            return new JoinRoomResponse(false, -1, result.ErrorMessage);
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, roomId);
        await Clients.Group(roomId).SendAsync("PlayerJoined", new PlayerJoinedEvent(UserId, UserName, result.SeatIndex));
        
        // Send current game state to the joining player
        var engine = serviceProvider.GetKeyedService<IGameEngine>(gameType);
        if (engine != null)
        {
            var state = await engine.GetStateAsync(roomId);
            if (state != null)
            {
                await Clients.Caller.SendAsync("GameState", state);
            }
        }

        logger.LogInformation("Player {UserId} joined room {RoomId}", UserId, roomId);
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

        var command = new GameCommand(UserId, actionName, payload);
        
        try
        {
            var result = await engine.ExecuteAsync(roomId, command);

            // Broadcast state if the action was successful and should broadcast
            if (result.Success && result.ShouldBroadcast && result.NewState != null)
            {
                await Clients.Group(roomId).SendAsync("GameState", result.NewState);
            }

            // Broadcast individual events
            foreach (var evt in result.Events)
            {
                await Clients.Group(roomId).SendAsync(evt.EventName, evt.Data);
            }

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
    }

    /// <summary>
    /// Get current game state
    /// </summary>
    public async Task<GameStateResponse?> GetState(string roomId)
    {
        var gameType = await roomRegistry.GetGameTypeAsync(roomId);
        if (gameType == null)
        {
            return null;
        }

        var engine = serviceProvider.GetKeyedService<IGameEngine>(gameType);
        return engine != null ? await engine.GetStateAsync(roomId) : null;
    }

    /// <summary>
    /// Get legal actions for current player
    /// </summary>
    public async Task<IReadOnlyList<string>> GetLegalActions(string roomId)
    {
        var gameType = await roomRegistry.GetGameTypeAsync(roomId);
        if (gameType == null)
        {
            return [];
        }

        var engine = serviceProvider.GetKeyedService<IGameEngine>(gameType);
        return engine != null ? await engine.GetLegalActionsAsync(roomId, UserId) : [];
    }
}

// Hub DTOs
public sealed record CreateRoomResponse(bool Success, string? RoomId, string? ErrorMessage);
public sealed record JoinRoomResponse(bool Success, int SeatIndex, string? ErrorMessage);
public sealed record PlayerJoinedEvent(string UserId, string UserName, int SeatIndex);
public sealed record PlayerLeftEvent(string UserId, string UserName);
public sealed record ActionErrorEvent(string Action, string ErrorMessage);