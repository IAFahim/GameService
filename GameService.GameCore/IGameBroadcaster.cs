namespace GameService.GameCore;

/// <summary>
/// Abstract interface for broadcasting game events to clients.
/// Decouples game modules from specific transport (SignalR).
/// </summary>
public interface IGameBroadcaster
{
    Task BroadcastStateAsync(string roomId, object state);
    Task BroadcastEventAsync(string roomId, GameEvent gameEvent);
    Task BroadcastResultAsync(string roomId, GameActionResult result);
}