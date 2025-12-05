namespace GameService.Sdk.Core;

// ═══════════════════════════════════════════════════════════════
// 🎮 CORE TYPES - Used across all games
// ═══════════════════════════════════════════════════════════════

/// <summary>Connection state</summary>
public enum ConnectionState { Disconnected, Connecting, Connected, Reconnecting }

/// <summary>Current game state snapshot</summary>
public sealed record GameState(
    string RoomId,
    string GameType,
    string Phase,
    string? CurrentTurnUserId,
    int PlayerCount,
    int MaxPlayers,
    IReadOnlyDictionary<string, int> PlayerSeats,
    object? GameData);

/// <summary>Result of creating a room</summary>
public sealed record CreateRoomResult(bool Success, string? RoomId, string? Error);

/// <summary>Result of joining a room</summary>
public sealed record JoinRoomResult(bool Success, int SeatIndex, string? Error);

/// <summary>Result of spectating</summary>
public sealed record SpectateResult(bool Success, string? Error);

/// <summary>Result of a game action</summary>
public sealed record ActionResult(bool Success, string? Error, object? NewState);

// ═══════════════════════════════════════════════════════════════
// 📨 EVENT TYPES - What you receive from the server
// ═══════════════════════════════════════════════════════════════

/// <summary>A player joined the room</summary>
public sealed record PlayerJoined(string UserId, string UserName, int SeatIndex);

/// <summary>A player left the room</summary>
public sealed record PlayerLeft(string UserId, string UserName);

/// <summary>A player disconnected (grace period active)</summary>
public sealed record PlayerDisconnected(string UserId, string UserName, int GracePeriodSeconds);

/// <summary>A player reconnected</summary>
public sealed record PlayerReconnected(string UserId, string UserName);

/// <summary>Chat message</summary>
public sealed record ChatMessage(string UserId, string UserName, string Message, DateTimeOffset Timestamp);

/// <summary>Generic game event</summary>
public sealed record GameEvent(string EventName, object Data, DateTimeOffset Timestamp);

/// <summary>Action error</summary>
public sealed record ActionError(string Action, string Message);

// ═══════════════════════════════════════════════════════════════
// 🔌 WIRE PROTOCOL TYPES - Internal SignalR payloads
// ═══════════════════════════════════════════════════════════════

internal sealed record CreateRoomResponse(bool Success, string? RoomId, string? ErrorMessage);
internal sealed record JoinRoomResponse(bool Success, int SeatIndex, string? ErrorMessage);
internal sealed record SpectateRoomResponse(bool Success, string? ErrorMessage);

internal sealed record GameActionResponse(
    bool Success, 
    string? ErrorMessage, 
    bool ShouldBroadcast,
    object? NewState);

internal sealed record GameStateResponse(
    string RoomId,
    string GameType,
    string Phase,
    string? CurrentTurnUserId,
    GameMetaResponse Meta,
    object? GameSpecificState);

internal sealed record GameMetaResponse(
    int CurrentPlayerCount,
    int MaxPlayers,
    IReadOnlyDictionary<string, int> PlayerSeats);

internal sealed record PlayerJoinedPayload(string UserId, string UserName, int SeatIndex);
internal sealed record PlayerLeftPayload(string UserId, string UserName);
internal sealed record PlayerDisconnectedPayload(string UserId, string UserName, int GracePeriodSeconds);
internal sealed record PlayerReconnectedPayload(string UserId, string UserName);
internal sealed record ChatMessagePayload(string UserId, string UserName, string Message, DateTimeOffset Timestamp);
internal sealed record GameEventPayload(string EventName, object Data, DateTimeOffset Timestamp);
internal sealed record ActionErrorPayload(string Action, string ErrorMessage);
