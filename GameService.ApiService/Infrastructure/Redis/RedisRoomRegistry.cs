using GameService.GameCore;
using StackExchange.Redis;

namespace GameService.ApiService.Infrastructure.Redis;

/// <summary>
/// Redis-based room registry for O(1) RoomId → GameType lookups
/// </summary>
public sealed class RedisRoomRegistry(IConnectionMultiplexer redis, ILogger<RedisRoomRegistry> logger) : IRoomRegistry
{
    private readonly IDatabase _db = redis.GetDatabase();

    private const string GlobalRegistryKey = "global:room_registry";

    private static string GameTypeRoomsKey(string gameType) => $"rooms:by_type:{gameType}";

    private static string LockKey(string roomId) => $"lock:room:{roomId}";

    public async Task<string?> GetGameTypeAsync(string roomId)
    {
        var gameType = await _db.HashGetAsync(GlobalRegistryKey, roomId);
        return gameType.IsNullOrEmpty ? null : gameType.ToString();
    }

    public async Task RegisterRoomAsync(string roomId, string gameType)
    {
        var batch = _db.CreateBatch();

        _ = batch.HashSetAsync(GlobalRegistryKey, roomId, gameType);

        _ = batch.SetAddAsync(GameTypeRoomsKey(gameType), roomId);
        
        batch.Execute();
        await Task.CompletedTask;

        logger.LogDebug("Registered room {RoomId} for game type {GameType}", roomId, gameType);
    }

    public async Task UnregisterRoomAsync(string roomId)
    {
        var gameType = await GetGameTypeAsync(roomId);
        if (gameType == null)
        {
            return;
        }

        var batch = _db.CreateBatch();

        _ = batch.HashDeleteAsync(GlobalRegistryKey, roomId);

        _ = batch.SetRemoveAsync(GameTypeRoomsKey(gameType), roomId);
        
        batch.Execute();
        
        logger.LogDebug("Unregistered room {RoomId}", roomId);
    }

    public async Task<IReadOnlyList<string>> GetAllRoomIdsAsync()
    {
        var entries = await _db.HashGetAllAsync(GlobalRegistryKey);
        return entries.Select(e => e.Name.ToString()).ToList();
    }

    public async Task<IReadOnlyList<string>> GetRoomIdsByGameTypeAsync(string gameType)
    {
        var members = await _db.SetMembersAsync(GameTypeRoomsKey(gameType));
        return members.Select(m => m.ToString()).ToList();
    }

    public async Task<(IReadOnlyList<string> RoomIds, long NextCursor)> GetRoomIdsPagedAsync(string gameType, long cursor = 0, int pageSize = 50)
    {
        var result = await _db.SetScanAsync(GameTypeRoomsKey(gameType), cursor: cursor, pageSize: pageSize).ToArrayAsync();

        var roomIds = result.Select(m => m.ToString()).ToList();

        var nextCursor = roomIds.Count == pageSize ? cursor + pageSize : 0;
        
        return (roomIds, nextCursor);
    }

    public async Task<bool> TryAcquireLockAsync(string roomId, TimeSpan timeout)
    {
        return await _db.StringSetAsync(
            LockKey(roomId),
            Environment.MachineName,
            timeout,
            When.NotExists);
    }

    public async Task ReleaseLockAsync(string roomId)
    {
        await _db.KeyDeleteAsync(LockKey(roomId));
    }
}
