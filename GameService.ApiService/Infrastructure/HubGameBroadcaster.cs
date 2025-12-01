using GameService.ApiService.Hubs;
using GameService.GameCore;
using Microsoft.AspNetCore.SignalR;

namespace GameService.ApiService.Infrastructure;

public class HubGameBroadcaster(IHubContext<GameHub> hubContext) : IGameBroadcaster
{
    public async Task BroadcastStateAsync(string roomId, object state)
    {
        await hubContext.Clients.Group(roomId).SendAsync("GameState", state);
    }

    public async Task BroadcastEventAsync(string roomId, GameEvent gameEvent)
    {
        await hubContext.Clients.Group(roomId).SendAsync(gameEvent.EventName, gameEvent.Data);
    }

    public async Task BroadcastResultAsync(string roomId, GameActionResult result)
    {
        if (result.ShouldBroadcast && result.NewState != null)
        {
            await BroadcastStateAsync(roomId, result.NewState);
        }

        foreach (var evt in result.Events)
        {
            await BroadcastEventAsync(roomId, evt);
        }
    }
}