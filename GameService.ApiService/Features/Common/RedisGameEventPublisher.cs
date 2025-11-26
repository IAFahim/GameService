using System.Text.Json;
using GameService.ServiceDefaults;
using GameService.ServiceDefaults.DTOs;
using StackExchange.Redis;

namespace GameService.ApiService.Features.Common;

public class RedisGameEventPublisher(IConnectionMultiplexer redis) : IGameEventPublisher
{
    public async Task PublishPlayerUpdatedAsync(PlayerUpdatedMessage message)
    {
        var json = JsonSerializer.Serialize(message, GameJsonContext.Default.PlayerUpdatedMessage);
        await redis.GetSubscriber().PublishAsync(RedisChannel.Literal(GameConstants.PlayerUpdatesChannel), json);
    }
}