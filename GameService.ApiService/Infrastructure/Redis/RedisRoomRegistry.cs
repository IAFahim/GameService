using GameService.GameCore;
using StackExchange.Redis;

namespace GameService.ApiService.Infrastructure.Redis;

public sealed class RedisRoomRegistry(IConnectionMultiplexer redis) : IRoomRegistry
{
    private const string GlobalRegistryKey = "global:room_registry";
    private const string UserRoomKey = "global:user_rooms";
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

    private static string GameTypeIndexKey(string gameType)
    {
        return $"index:rooms:{gameType}";
    }

    private static string LockKey(string roomId)
    {
        return $"lock:room:{roomId}";
    }
}