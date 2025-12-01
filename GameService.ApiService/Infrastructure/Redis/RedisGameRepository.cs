using System.Runtime.CompilerServices;
using System.Text.Json;
using GameService.GameCore;
using StackExchange.Redis;

namespace GameService.ApiService.Infrastructure.Redis;

/// <summary>
/// Generic Redis repository for game state persistence.
/// Works with any game type that has a struct-based state.
/// </summary>
public sealed class RedisGameRepository<TState>(
    IConnectionMultiplexer redis,
    IRoomRegistry roomRegistry,
    string gameType,
    ILogger logger) : IGameRepository<TState> 
    where TState : struct
{
    private readonly IDatabase _db = redis.GetDatabase();

    private string StateKey(string roomId) => $"{{game:{gameType}}}:{roomId}:state";
    private string MetaKey(string roomId) => $"{{game:{gameType}}}:{roomId}:meta";
    private string LockKey(string roomId) => $"{{game:{gameType}}}:{roomId}:lock";

    public async Task<GameContext<TState>?> LoadAsync(string roomId)
    {
        var batch = _db.CreateBatch();
        var stateTask = batch.StringGetAsync(StateKey(roomId));
        var metaTask = batch.StringGetAsync(MetaKey(roomId));
        batch.Execute();
        
        await Task.WhenAll(stateTask, metaTask);
        
        if (stateTask.Result.IsNullOrEmpty)
        {
            return null;
        }

        var state = DeserializeState((byte[])stateTask.Result!);
        var meta = metaTask.Result.IsNullOrEmpty 
            ? new GameRoomMeta { GameType = gameType }
            : JsonSerializer.Deserialize<GameRoomMeta>(metaTask.Result.ToString()) ?? new GameRoomMeta { GameType = gameType };
        
        return new GameContext<TState>(roomId, state, meta);
    }

    public async Task SaveAsync(string roomId, TState state, GameRoomMeta meta)
    {
        var stateBytes = SerializeState(state);
        var metaJson = JsonSerializer.Serialize(meta);
        
        var batch = _db.CreateBatch();
        _ = batch.StringSetAsync(StateKey(roomId), stateBytes);
        _ = batch.StringSetAsync(MetaKey(roomId), metaJson);
        batch.Execute();

        await roomRegistry.RegisterRoomAsync(roomId, gameType);
        
        logger.LogDebug("Saved game state for room {RoomId}", roomId);
    }

    public async Task DeleteAsync(string roomId)
    {
        await _db.KeyDeleteAsync([StateKey(roomId), MetaKey(roomId), LockKey(roomId)]);
        await roomRegistry.UnregisterRoomAsync(roomId);
        
        logger.LogDebug("Deleted game state for room {RoomId}", roomId);
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

    /// <summary>
    /// Serialize struct to bytes using unsafe memory copy.
    /// This is much faster than JSON for game state updates.
    /// </summary>
    private static byte[] SerializeState(TState state)
    {
        var bytes = new byte[Unsafe.SizeOf<TState>()];
        Unsafe.WriteUnaligned(ref bytes[0], state);
        return bytes;
    }

    /// <summary>
    /// Deserialize bytes to struct using unsafe memory copy.
    /// </summary>
    private static TState DeserializeState(byte[] bytes)
    {
        return Unsafe.ReadUnaligned<TState>(ref bytes[0]);
    }
}

/// <summary>
/// Factory for creating typed game repositories
/// </summary>
public sealed class RedisGameRepositoryFactory(
    IConnectionMultiplexer redis,
    IRoomRegistry roomRegistry,
    ILoggerFactory loggerFactory) : IGameRepositoryFactory
{
    public IGameRepository<TState> Create<TState>(string gameType) where TState : struct
    {
        var logger = loggerFactory.CreateLogger<RedisGameRepository<TState>>();
        return new RedisGameRepository<TState>(redis, roomRegistry, gameType, logger);
    }
}
