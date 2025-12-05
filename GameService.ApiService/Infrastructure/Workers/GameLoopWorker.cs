using GameService.GameCore;
using GameService.ServiceDefaults.Configuration;
using Microsoft.Extensions.Options;

namespace GameService.ApiService.Infrastructure.Workers;

/// <summary>
///     Background worker that checks for turn timeouts across all active games.
///     Uses Redis Sorted Sets with activity timestamps for efficient timeout detection.
///     Only processes rooms that haven't had recent activity.
/// </summary>
public sealed class GameLoopWorker(
    IServiceProvider serviceProvider,
    IRoomRegistry roomRegistry,
    IGameBroadcaster broadcaster,
    IOptions<GameServiceOptions> options,
    ILogger<GameLoopWorker> logger) : BackgroundService
{
    private readonly int _tickIntervalMs = options.Value.GameLoop.TickIntervalMs;
    private const int MaxRoomsPerTick = 50; // Process up to 50 rooms per tick to avoid blocking

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("GameLoopWorker started - monitoring for turn timeouts");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckRoomsForTimeoutsOptimized(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in GameLoopWorker tick");
            }

            await Task.Delay(_tickIntervalMs, stoppingToken);
        }
    }

    private async Task CheckRoomsForTimeoutsOptimized(CancellationToken ct)
    {
        var modules = serviceProvider.GetServices<IGameModule>();

        foreach (var module in modules)
        {
            var engine = serviceProvider.GetKeyedService<IGameEngine>(module.GameName);

            if (engine is not ITurnBasedGameEngine turnEngine)
                continue;

            try
            {
                // Get rooms sorted by oldest activity (most likely to have timeouts)
                var roomIds = await roomRegistry.GetRoomsNeedingTimeoutCheckAsync(
                    module.GameName, 
                    MaxRoomsPerTick);

                if (roomIds.Count == 0) continue;

                foreach (var roomId in roomIds)
                {
                    if (ct.IsCancellationRequested) return;

                    try
                    {
                        if (!await roomRegistry.TryAcquireLockAsync(roomId, TimeSpan.FromSeconds(1))) 
                            continue;

                        try
                        {
                            var result = await turnEngine.CheckTimeoutsAsync(roomId);

                            if (result != null && result.Success)
                            {
                                logger.LogInformation("Timeout action executed in room {RoomId}: {EventCount} events",
                                    roomId, result.Events.Count);

                                await broadcaster.BroadcastResultAsync(roomId, result);
                                
                                // Update activity timestamp after processing
                                await roomRegistry.UpdateRoomActivityAsync(roomId, module.GameName);
                            }
                        }
                        finally
                        {
                            await roomRegistry.ReleaseLockAsync(roomId);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Error checking timeout for room {RoomId}", roomId);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing timeouts for game type {GameType}", module.GameName);
            }
        }
    }
}