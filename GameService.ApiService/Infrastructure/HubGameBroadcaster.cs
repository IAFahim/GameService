using GameService.ApiService.Hubs;
using GameService.GameCore;
using Microsoft.AspNetCore.SignalR;

namespace GameService.ApiService.Infrastructure;

public class HubGameBroadcaster(IHubContext<GameHub, IGameClient> hubContext, ILogger<HubGameBroadcaster> logger) : IGameBroadcaster
{
    public async Task BroadcastStateAsync(string roomId, object state)
    {
        if (state is GameStateResponse gameState)
        {
            await hubContext.Clients.Group(roomId).GameState(gameState);
        }
        else
        {
            logger.LogWarning("BroadcastStateAsync received invalid state type {Type} for room {RoomId}", state?.GetType().Name, roomId);
        }
    }

    public async Task BroadcastEventAsync(string roomId, GameEvent gameEvent)
    {
        await hubContext.Clients.Group(roomId).GameEvent(
            new GameEventPayload(gameEvent.EventName, gameEvent.Data, gameEvent.Timestamp));
    }

    public async Task BroadcastResultAsync(string roomId, GameActionResult result)
    {
        if (result.ShouldBroadcast && result.NewState != null)
            await BroadcastStateAsync(roomId, result.NewState);

        foreach (var evt in result.Events)
            await BroadcastEventAsync(roomId, evt);
    }
}