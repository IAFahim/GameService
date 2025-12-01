using System.Text.Json;

namespace GameService.GameCore;

/// <summary>
/// Core game engine interface - handles all game actions via command pattern.
/// Each game type registers ONE engine with a keyed service.
/// </summary>
public interface IGameEngine
{
    /// <summary>
    /// Unique identifier for this game type (e.g., "Ludo", "Chess", "Poker")
    /// </summary>
    string GameType { get; }
    
    /// <summary>
    /// Execute any game action (Roll, Move, Bet, Draw, etc.)
    /// </summary>
    Task<GameActionResult> ExecuteAsync(string roomId, GameCommand command);
    
    /// <summary>
    /// Get legal actions for the current player in a room
    /// </summary>
    Task<IReadOnlyList<string>> GetLegalActionsAsync(string roomId, string userId);
    
    /// <summary>
    /// Get the current game state for a room
    /// </summary>
    Task<GameStateResponse?> GetStateAsync(string roomId);
}

/// <summary>
/// Command sent from client to execute a game action
/// </summary>
public sealed record GameCommand(
    string UserId,
    string Action,
    JsonElement Payload)
{
    public T? GetPayload<T>() where T : class
    {
        if (Payload.ValueKind == JsonValueKind.Undefined || Payload.ValueKind == JsonValueKind.Null)
            return null;
        return Payload.Deserialize<T>();
    }
    
    public int GetInt(string propertyName, int defaultValue = 0)
    {
        if (Payload.TryGetProperty(propertyName, out var prop) && prop.TryGetInt32(out var value))
            return value;
        return defaultValue;
    }
}

/// <summary>
/// Result of executing a game action
/// </summary>
public sealed record GameActionResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public bool ShouldBroadcast { get; init; }
    public object? NewState { get; init; }
    public IReadOnlyList<GameEvent> Events { get; init; } = [];
    
    public static GameActionResult Error(string message) => 
        new() { Success = false, ErrorMessage = message, ShouldBroadcast = false, Events = [] };
    
    public static GameActionResult Ok(object? state = null, params GameEvent[] events) =>
        new() { Success = true, ShouldBroadcast = true, NewState = state, Events = events };
    
    public static GameActionResult OkNoState(params GameEvent[] events) =>
        new() { Success = true, ShouldBroadcast = false, Events = events };
}

/// <summary>
/// Event emitted by game engine for broadcasting to clients and audit logging
/// </summary>
public sealed record GameEvent(string EventName, object Data)
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Standardized game state response for API/SignalR
/// </summary>
public sealed record GameStateResponse
{
    public required string RoomId { get; init; }
    public required string GameType { get; init; }
    public required GameRoomMeta Meta { get; init; }
    public required object State { get; init; }
    public IReadOnlyList<string> LegalMoves { get; init; } = [];
}

/// <summary>
/// Common room metadata shared across all game types
/// </summary>
public sealed record GameRoomMeta
{
    public Dictionary<string, int> PlayerSeats { get; init; } = new();
    public bool IsPublic { get; init; } = true;
    public string GameType { get; init; } = "";
    public int MaxPlayers { get; init; } = 4;
    
    // NEW: Financials and Rule Config
    public long EntryFee { get; init; } = 0; 
    public Dictionary<string, string> Config { get; init; } = new();

    public int CurrentPlayerCount => PlayerSeats.Count;
}