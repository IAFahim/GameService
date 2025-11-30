using System.Text.Json;
using System.Threading.Channels;
using GameService.ServiceDefaults;
using GameService.ServiceDefaults.DTOs;
using GameService.Web.Services;
using StackExchange.Redis;

namespace GameService.Web.Workers;

public class RedisLogStreamer(
    IConnectionMultiplexer redis, 
    PlayerUpdateNotifier notifier, 
    ILogger<RedisLogStreamer> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions _jsonOptions = new() 
    { 
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true
    };
    
    // Throttle updates to max once per 500ms to prevent UI freezing at scale
    private static readonly TimeSpan ThrottleInterval = TimeSpan.FromMilliseconds(500);
    private readonly Channel<PlayerUpdatedMessage> _updateChannel = Channel.CreateBounded<PlayerUpdatedMessage>(
        new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Start the throttled consumer
        _ = Task.Run(() => ProcessUpdatesThrottledAsync(stoppingToken), stoppingToken);
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var sub = redis.GetSubscriber();
                var channel = await sub.SubscribeAsync(RedisChannel.Literal(GameConstants.PlayerUpdatesChannel));

                logger.LogInformation("✅ [RedisLogStreamer] Connected and listening on channel: {Channel}", GameConstants.PlayerUpdatesChannel);

                await foreach (var message in channel.WithCancellation(stoppingToken))
                {
                    if (message.Message.IsNullOrEmpty) continue;

                    try
                    {
                        var payload = (string)message.Message!;
                        logger.LogDebug("⚡ [RedisLogStreamer] Received: {Payload}", payload);

                        var update = JsonSerializer.Deserialize<PlayerUpdatedMessage>(payload, _jsonOptions);

                        if (update != null)
                        {
                            // Queue for throttled processing instead of immediate notify
                            _updateChannel.Writer.TryWrite(update);
                        }
                    }
                    catch (JsonException jex)
                    {
                        logger.LogError(jex, "❌ [RedisLogStreamer] JSON Deserialization failed for message: {Message}", message.Message);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "❌ [RedisLogStreamer] Error processing message");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "⚠️ [RedisLogStreamer] Connection failed. Retrying in 5s...");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }
    
    private async Task ProcessUpdatesThrottledAsync(CancellationToken stoppingToken)
    {
        var pendingUpdates = new Dictionary<string, PlayerUpdatedMessage>();
        var lastFlush = DateTime.UtcNow;
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Try to read with timeout
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                cts.CancelAfter(ThrottleInterval);
                
                try
                {
                    while (await _updateChannel.Reader.WaitToReadAsync(cts.Token))
                    {
                        while (_updateChannel.Reader.TryRead(out var update))
                        {
                            // Keep only the latest update per user (coalesce rapid updates)
                            pendingUpdates[update.UserId] = update;
                        }
                    }
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Timeout reached, flush pending updates
                }
                
                // Flush if we have updates and throttle interval passed
                if (pendingUpdates.Count > 0 && (DateTime.UtcNow - lastFlush) >= ThrottleInterval)
                {
                    foreach (var update in pendingUpdates.Values)
                    {
                        notifier.Notify(update);
                    }
                    
                    logger.LogDebug("📤 [RedisLogStreamer] Flushed {Count} throttled updates", pendingUpdates.Count);
                    pendingUpdates.Clear();
                    lastFlush = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "❌ [RedisLogStreamer] Error in throttled processor");
            }
        }
    }
}