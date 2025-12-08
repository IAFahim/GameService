using GameService.GameCore;
using GameService.ServiceDefaults.Data;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace GameService.ApiService.Infrastructure.Workers;


public sealed class GameStateSnapshotWorker(
    IServiceProvider serviceProvider,
    IConnectionMultiplexer redis,
    IRoomRegistry roomRegistry,
    ILogger<GameStateSnapshotWorker> logger) : BackgroundService
{
    private const int BatchSize = 50;
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("GameStateSnapshotWorker started - snapshotting every {Interval}", SnapshotInterval);

        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SnapshotActiveGamesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during game state snapshot");
            }

            await Task.Delay(SnapshotInterval, stoppingToken);
        }
    }

    private async Task SnapshotActiveGamesAsync(CancellationToken ct)
    {
        var roomIds = await roomRegistry.GetAllRoomIdsAsync();
        if (roomIds.Count == 0) return;

        var db = redis.GetDatabase();
        var snapshotCount = 0;
        var errorCount = 0;

        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GameDbContext>();

        foreach (var batch in roomIds.Chunk(BatchSize))
        {
            if (ct.IsCancellationRequested) break;

            foreach (var roomId in batch)
                try
                {
                    var gameType = await roomRegistry.GetGameTypeAsync(roomId);
                    if (gameType == null) continue;

                    var stateKey = $"game:{gameType}:{{{roomId}}}:state";
                    var metaKey = $"game:{gameType}:{{{roomId}}}:meta";

                    var redisBatch = db.CreateBatch();
                    var stateTask = redisBatch.StringGetAsync(stateKey);
                    var metaTask = redisBatch.StringGetAsync(metaKey);
                    redisBatch.Execute();

                    await Task.WhenAll(stateTask, metaTask);

                    if (stateTask.Result.IsNullOrEmpty) continue;

                    var stateBytes = (byte[])stateTask.Result!;
                    var metaJson = metaTask.Result.IsNullOrEmpty ? "{}" : metaTask.Result.ToString();

                    var existing = await dbContext.GameStateSnapshots
                        .FirstOrDefaultAsync(s => s.RoomId == roomId, ct);

                    if (existing != null)
                    {
                        existing.GameType = gameType;
                        existing.StateData = stateBytes;
                        existing.MetaJson = metaJson;
                        existing.SnapshotAt = DateTimeOffset.UtcNow;
                    }
                    else
                    {
                        dbContext.GameStateSnapshots.Add(new GameStateSnapshot
                        {
                            RoomId = roomId,
                            GameType = gameType,
                            StateData = stateBytes,
                            MetaJson = metaJson,
                            SnapshotAt = DateTimeOffset.UtcNow
                        });
                    }

                    snapshotCount++;
                }
                catch (Exception ex)
                {
                    errorCount++;
                    logger.LogWarning(ex, "Failed to snapshot room {RoomId}", roomId);
                }

            if (snapshotCount > 0) await dbContext.SaveChangesAsync(ct);
        }

        if (snapshotCount > 0)
            logger.LogInformation(
                "Game state snapshot completed: {SnapshotCount} rooms saved, {ErrorCount} errors",
                snapshotCount, errorCount);

        await CleanupStaleSnapshotsAsync(dbContext, roomIds, ct);
    }

    private async Task CleanupStaleSnapshotsAsync(
        GameDbContext dbContext,
        IReadOnlyList<string> activeRoomIds,
        CancellationToken ct)
    {
        var activeSet = activeRoomIds.ToHashSet();
        var cutoff = DateTimeOffset.UtcNow.AddHours(-1);

        var deleted = await dbContext.GameStateSnapshots
            .Where(s => s.SnapshotAt < cutoff && !activeSet.Contains(s.RoomId))
            .ExecuteDeleteAsync(ct);

        if (deleted > 0) logger.LogInformation("Cleaned up {Count} stale game state snapshots", deleted);
    }
}