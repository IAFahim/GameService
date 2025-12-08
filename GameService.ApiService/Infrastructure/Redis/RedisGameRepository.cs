using System.Runtime.CompilerServices;
using System.Text.Json;
using GameService.GameCore;
using StackExchange.Redis;

namespace GameService.ApiService.Infrastructure.Redis;

public interface IStateMigration<TState> where TState : struct
{
   byte FromVersion { get; }
    byte ToVersion { get; }
    int FromSize { get; }
    bool TryMigrate(ReadOnlySpan<byte> oldData, out TState newState);
}

public interface IStateMigrationRegistry
{
    void Register<TState>(IStateMigration<TState> migration) where TState : struct;
    IStateMigration<TState>? GetMigration<TState>(byte fromVersion, int fromSize) where TState : struct;
}

public sealed class StateMigrationRegistry : IStateMigrationRegistry
{
    private readonly Dictionary<(Type, byte, int), object> _migrations = new();

    public void Register<TState>(IStateMigration<TState> migration) where TState : struct
    {
        _migrations[(typeof(TState), migration.FromVersion, migration.FromSize)] = migration;
    }

    public IStateMigration<TState>? GetMigration<TState>(byte fromVersion, int fromSize) where TState : struct
    {
        return _migrations.TryGetValue((typeof(TState), fromVersion, fromSize), out var m)
            ? m as IStateMigration<TState>
            : null;
    }
}

public sealed class RedisGameRepository<TState>(
    IConnectionMultiplexer redis,
    IRoomRegistry roomRegistry,
    string gameType,
    ILogger logger,
    IStateMigrationRegistry? migrationRegistry = null) : IGameRepository<TState>
    where TState : struct
{
    private const byte CurrentVersion = 1;

    private static readonly int StateSize = Unsafe.SizeOf<TState>();
    private readonly IDatabase _db = redis.GetDatabase();

    public async Task<GameContext<TState>?> LoadAsync(string roomId)
    {
        var batch = _db.CreateBatch();
        var stateTask = batch.StringGetAsync(StateKey(roomId));
        var metaTask = batch.StringGetAsync(MetaKey(roomId));
        batch.Execute();

        await Task.WhenAll(stateTask, metaTask);

        if (stateTask.Result.IsNullOrEmpty) return null;

        try
        {
            var bytes = (byte[])stateTask.Result!;
            var state = DeserializeStateWithMigration(bytes, roomId);

            var meta = metaTask.Result.IsNullOrEmpty
                ? new GameRoomMeta { GameType = gameType }
                : JsonSerializer.Deserialize<GameRoomMeta>(metaTask.Result.ToString()) ??
                  new GameRoomMeta { GameType = gameType };

            return new GameContext<TState>(roomId, state, meta);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "State corruption detected for room {RoomId}. Resetting state.", roomId);
            return null;
        }
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
    }

    public async Task<IReadOnlyList<GameContext<TState>>> LoadManyAsync(IReadOnlyList<string> roomIds)
    {
        if (roomIds.Count == 0) return [];

        var stateKeys = roomIds.Select(StateKey).ToArray();
        var metaKeys = roomIds.Select(MetaKey).ToArray();

        var stateValues = await _db.StringGetAsync(stateKeys.Select(k => (RedisKey)k).ToArray());
        var metaValues = await _db.StringGetAsync(metaKeys.Select(k => (RedisKey)k).ToArray());

        var results = new List<GameContext<TState>>(roomIds.Count);

        for (var i = 0; i < roomIds.Count; i++)
        {
            if (stateValues[i].IsNullOrEmpty) continue;

            try
            {
                var bytes = (byte[])stateValues[i]!;
                var state = DeserializeStateWithMigration(bytes, roomIds[i]);

                var meta = metaValues[i].IsNullOrEmpty
                    ? new GameRoomMeta { GameType = gameType }
                    : JsonSerializer.Deserialize<GameRoomMeta>(metaValues[i].ToString()) ??
                      new GameRoomMeta { GameType = gameType };

                results.Add(new GameContext<TState>(roomIds[i], state, meta));
            }
            catch (InvalidOperationException ex)
            {
                logger.LogError(ex, "State corruption detected for room {RoomId} during batch load.", roomIds[i]);
            }
        }

        return results;
    }

    public async Task DeleteAsync(string roomId)
    {
        await _db.KeyDeleteAsync([StateKey(roomId), MetaKey(roomId), LockKey(roomId)]);
        await roomRegistry.UnregisterRoomAsync(roomId);
    }

    public async Task<bool> TryAcquireLockAsync(string roomId, TimeSpan timeout)
    {
        return await _db.StringSetAsync(LockKey(roomId), Environment.MachineName, timeout, When.NotExists);
    }

    public async Task ReleaseLockAsync(string roomId)
    {
        await _db.KeyDeleteAsync(LockKey(roomId));
    }

    private TState DeserializeStateWithMigration(byte[] bytes, string roomId)
    {
        if (bytes.Length < 5)
            throw new InvalidOperationException("Invalid state data: too short");

        var storedVersion = bytes[0];
        var storedSize = Unsafe.ReadUnaligned<int>(ref bytes[1]);

        if (storedVersion == CurrentVersion && storedSize == StateSize)
            return Unsafe.ReadUnaligned<TState>(ref bytes[5]);

        if (migrationRegistry != null)
        {
            var migration = migrationRegistry.GetMigration<TState>(storedVersion, storedSize);
            if (migration != null)
            {
                var oldData = bytes.AsSpan(5, storedSize);
                if (migration.TryMigrate(oldData, out var newState))
                {
                    logger.LogInformation(
                        "Migrated room {RoomId} state from v{FromVersion} ({FromSize}B) to v{ToVersion} ({ToSize}B)",
                        roomId, storedVersion, storedSize, migration.ToVersion, StateSize);
                    return newState;
                }
            }
        }

        throw new InvalidOperationException(
            $"Cannot load state for room {roomId}. " +
            $"Stored: v{storedVersion} ({storedSize}B), Current: v{CurrentVersion} ({StateSize}B). " +
            $"No migration registered. Consider deploying a migration or resetting the room.");
    }

    private string StateKey(string roomId)
    {
        return $"game:{gameType}:{{{roomId}}}:state";
    }

    private string MetaKey(string roomId)
    {
        return $"game:{gameType}:{{{roomId}}}:meta";
    }

    private string LockKey(string roomId)
    {
        return $"game:{gameType}:{{{roomId}}}:lock";
    }

    private static byte[] SerializeState(TState state)
    {
        var bytes = new byte[1 + 4 + StateSize];
        bytes[0] = CurrentVersion;
        Unsafe.WriteUnaligned(ref bytes[1], StateSize);
        Unsafe.WriteUnaligned(ref bytes[5], state);
        return bytes;
    }
}

public sealed class RedisGameRepositoryFactory(
    IConnectionMultiplexer redis,
    IRoomRegistry roomRegistry,
    ILoggerFactory loggerFactory,
    IStateMigrationRegistry? migrationRegistry = null) : IGameRepositoryFactory
{
    public IGameRepository<TState> Create<TState>(string gameType) where TState : struct
    {
        var logger = loggerFactory.CreateLogger<RedisGameRepository<TState>>();
        return new RedisGameRepository<TState>(redis, roomRegistry, gameType, logger, migrationRegistry);
    }
}