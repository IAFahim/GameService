using GameService.GameCore;
using StackExchange.Redis;

namespace GameService.ApiService.Infrastructure.Redis;

/// <summary>
/// Redis-based room registry for O(1) RoomId → GameType lookups
/// </summary>
public sealed class RedisRoomRegistry(IConnectionMultiplexer redis, ILogger<RedisRoomRegistry> logger) : IRoomRegistry
{
    private readonly IDatabase _db = redis.GetDatabase();
    
    // Global registry for all rooms: Hash of RoomId → GameType
    private const string GlobalRegistryKey = "global:room_registry";
    
    // Per-game-type sets for listing rooms by type
    private static string GameTypeRoomsKey(string gameType) => $"rooms:by_type:{gameType}";

    public async Task<string?> GetGameTypeAsync(string roomId)
    {
        var gameType = await _db.HashGetAsync(GlobalRegistryKey, roomId);
        return gameType.IsNullOrEmpty ? null : gameType.ToString();
    }

    public async Task RegisterRoomAsync(string roomId, string gameType)
    {
        var batch = _db.CreateBatch();
        
        // Add to global registry
        _ = batch.HashSetAsync(GlobalRegistryKey, roomId, gameType);
        
        // Add to game-type-specific set
        _ = batch.SetAddAsync(GameTypeRoomsKey(gameType), roomId);
        
        batch.Execute();
        await Task.CompletedTask; // Batch is fire-and-forget, but we wait for consistency
        
        logger.LogDebug("Registered room {RoomId} for game type {GameType}", roomId, gameType);
    }

    public async Task UnregisterRoomAsync(string roomId)
    {
        // First get the game type so we can remove from the type-specific set
        var gameType = await GetGameTypeAsync(roomId);
        if (gameType == null)
        {
            return;
        }

        var batch = _db.CreateBatch();
        
        // Remove from global registry
        _ = batch.HashDeleteAsync(GlobalRegistryKey, roomId);
        
        // Remove from game-type-specific set
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
}
