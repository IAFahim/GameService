using GameService.GameCore;
using StackExchange.Redis;

namespace GameService.ApiService.Infrastructure.Redis;

public sealed class RedisRoomRegistry(IConnectionMultiplexer redis) : IRoomRegistry
{
    private const string GlobalRegistryKey = "global:room_registry";
    private const string UserRoomKey = "global:user_rooms";
    private const string DisconnectedPlayersKey = "global:disconnected_players";
    private const string UserConnectionCountKey = "global:user_connections";
    private const string RateLimitKeyPrefix = "ratelimit:";
    private readonly IDatabase _db = redis.GetDatabase();

    public async Task<string?> GetGameTypeAsync(string roomId)
    {
        var gameType = await _db.HashGetAsync(GlobalRegistryKey, roomId);
        return gameType.IsNullOrEmpty ? null : gameType.ToString();
    }

    public async Task RegisterRoomAsync(string roomId, string gameType)
    {
        var batch = _db.CreateBatch();
        var score = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        _ = batch.HashSetAsync(GlobalRegistryKey, roomId, gameType);
        _ = batch.SortedSetAddAsync(GameTypeIndexKey(gameType), roomId, score);
        _ = batch.SortedSetAddAsync(ActivityIndexKey(gameType), roomId, score);

        batch.Execute();
        await Task.CompletedTask;
    }

    public async Task UnregisterRoomAsync(string roomId)
    {
        var gameType = await GetGameTypeAsync(roomId);
        if (gameType == null) return;

        var batch = _db.CreateBatch();
        _ = batch.HashDeleteAsync(GlobalRegistryKey, roomId);
        _ = batch.SortedSetRemoveAsync(GameTypeIndexKey(gameType), roomId);
        _ = batch.SortedSetRemoveAsync(ActivityIndexKey(gameType), roomId);
        batch.Execute();
    }

    public async Task<IReadOnlyList<string>> GetAllRoomIdsAsync()
    {
        var entries = await _db.HashKeysAsync(GlobalRegistryKey);
        return entries.Select(e => e.ToString()).ToList();
    }

    public async Task<IReadOnlyList<string>> GetRoomIdsByGameTypeAsync(string gameType)
    {
        var members = await _db.SortedSetRangeByRankAsync(GameTypeIndexKey(gameType), 0, 999, Order.Descending);
        return members.Select(m => m.ToString()).ToList();
    }

    public async Task<(IReadOnlyList<string> RoomIds, long NextCursor)> GetRoomIdsPagedAsync(string gameType,
        long cursor = 0, int pageSize = 50)
    {
        var start = cursor;
        var stop = cursor + pageSize - 1;

        var members = await _db.SortedSetRangeByRankAsync(GameTypeIndexKey(gameType), start, stop, Order.Descending);

        var roomIds = members.Select(m => m.ToString()).ToList();

        var nextCursor = roomIds.Count == pageSize ? cursor + pageSize : 0;

        return (roomIds, nextCursor);
    }

    public async Task<bool> TryAcquireLockAsync(string roomId, TimeSpan timeout)
    {
        return await _db.StringSetAsync(LockKey(roomId), Environment.MachineName, timeout, When.NotExists);
    }

    public async Task ReleaseLockAsync(string roomId)
    {
        await _db.KeyDeleteAsync(LockKey(roomId));
    }

    public async Task SetUserRoomAsync(string userId, string roomId)
    {
        await _db.HashSetAsync(UserRoomKey, userId, roomId);
    }

    public async Task<string?> GetUserRoomAsync(string userId)
    {
        var roomId = await _db.HashGetAsync(UserRoomKey, userId);
        return roomId.IsNullOrEmpty ? null : roomId.ToString();
    }

    public async Task RemoveUserRoomAsync(string userId)
    {
        await _db.HashDeleteAsync(UserRoomKey, userId);
    }

    public async Task SetDisconnectedPlayerAsync(string userId, string roomId, TimeSpan gracePeriod)
    {
        // Store with TTL for automatic cleanup
        var key = $"{DisconnectedPlayersKey}:{userId}";
        await _db.StringSetAsync(key, roomId, gracePeriod);
    }

    public async Task<string?> TryGetAndRemoveDisconnectedPlayerAsync(string userId)
    {
        var key = $"{DisconnectedPlayersKey}:{userId}";
        var roomId = await _db.StringGetDeleteAsync(key);
        return roomId.IsNullOrEmpty ? null : roomId.ToString();
    }

    public async Task<bool> CheckRateLimitAsync(string userId, int maxPerMinute)
    {
        var key = $"{RateLimitKeyPrefix}{userId}";
        var count = await _db.StringIncrementAsync(key);
        
        if (count == 1)
        {
            // Set expiry on first request in window
            await _db.KeyExpireAsync(key, TimeSpan.FromMinutes(1));
        }
        
        return count <= maxPerMinute;
    }

    public async Task<int> IncrementConnectionCountAsync(string userId)
    {
        var count = await _db.HashIncrementAsync(UserConnectionCountKey, userId);
        return (int)count;
    }

    public async Task DecrementConnectionCountAsync(string userId)
    {
        var count = await _db.HashDecrementAsync(UserConnectionCountKey, userId);
        if (count <= 0)
        {
            await _db.HashDeleteAsync(UserConnectionCountKey, userId);
        }
    }

    public async Task<IReadOnlyList<string>> GetRoomsNeedingTimeoutCheckAsync(string gameType, int maxRooms)
    {
        // Get rooms sorted by oldest activity first (most likely to have timeouts)
        var members = await _db.SortedSetRangeByRankAsync(
            ActivityIndexKey(gameType), 
            0, 
            maxRooms - 1, 
            Order.Ascending);
        
        return members.Select(m => m.ToString()).ToList();
    }

    public async Task UpdateRoomActivityAsync(string roomId, string gameType)
    {
        var score = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await _db.SortedSetAddAsync(ActivityIndexKey(gameType), roomId, score);
    }

    private static string GameTypeIndexKey(string gameType) => $"index:rooms:{gameType}";
    private static string ActivityIndexKey(string gameType) => $"index:activity:{gameType}";
    private static string LockKey(string roomId) => $"lock:room:{roomId}";
}