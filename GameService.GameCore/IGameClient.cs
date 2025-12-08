namespace GameService.GameCore;

/// <summary>
///     Strongly typed SignalR client interface.
///     Prevents typos in SendAsync method names and provides compile-time safety.
/// </summary>
public interface IGameClient
{
    /// <summary>
    ///     Broadcast the current game state to all clients in a room
    /// </summary>
    Task GameState(GameStateResponse state);

    /// <summary>
    ///     Notify when a player joins a room
    /// </summary>
    Task PlayerJoined(PlayerJoinedPayload payload);

    /// <summary>
    ///     Notify when a player leaves a room
    /// </summary>
    Task PlayerLeft(PlayerLeftPayload payload);

    /// <summary>
    ///     Notify when a player disconnects (grace period starts)
    /// </summary>
    Task PlayerDisconnected(PlayerDisconnectedPayload payload);

    /// <summary>
    ///     Notify when a player reconnects within grace period
    /// </summary>
    Task PlayerReconnected(PlayerReconnectedPayload payload);

    /// <summary>
    ///     Send an action error to the caller
    /// </summary>
    Task ActionError(ActionErrorPayload payload);

    /// <summary>
    ///     Send a chat message to all players in a room
    /// </summary>
    Task ChatMessage(ChatMessagePayload payload);

    /// <summary>
    ///     Broadcast a generic game event (DiceRolled, TokenMoved, etc.)
    /// </summary>
    Task GameEvent(GameEventPayload payload);
}

public sealed record PlayerJoinedPayload(string UserId, string UserName, int SeatIndex);

public sealed record PlayerLeftPayload(string UserId, string UserName);

public sealed record PlayerDisconnectedPayload(string UserId, string UserName, int GracePeriodSeconds);

public sealed record PlayerReconnectedPayload(string UserId, string UserName);

public sealed record ActionErrorPayload(string Action, string ErrorMessage);

public sealed record ChatMessagePayload(string UserId, string UserName, string Message, DateTimeOffset Timestamp);

public sealed record GameEventPayload(string EventName, object Data, DateTimeOffset Timestamp);
