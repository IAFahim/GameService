using System.Text.Json;
using GameService.ApiService.Features.Games;
using GameService.ApiService.Hubs;
using GameService.ServiceDefaults;
using GameService.ServiceDefaults.Data;
using GameService.ServiceDefaults.DTOs;
using Microsoft.EntityFrameworkCore;

namespace GameService.ApiService.Infrastructure.Workers;

public sealed class OutboxProcessorWorker(
    IServiceProvider serviceProvider,
    IGameEventPublisher publisher,
    ILogger<OutboxProcessorWorker> logger) : BackgroundService
{
    private const int BatchSize = 100;
    private const int MaxAttempts = 5;
    private static readonly TimeSpan ProcessingInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(7);

    private DateTime _lastCleanup = DateTime.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("OutboxProcessorWorker started");

        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingMessagesAsync(stoppingToken);

                if (DateTime.UtcNow - _lastCleanup > CleanupInterval)
                {
                    await CleanupOldMessagesAsync(stoppingToken);
                    _lastCleanup = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in OutboxProcessorWorker");
            }

            await Task.Delay(ProcessingInterval, stoppingToken);
        }
    }

    private async Task ProcessPendingMessagesAsync(CancellationToken ct)
    {
        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();

        var pendingMessages = await db.OutboxMessages
            .Where(m => m.ProcessedAt == null && m.Attempts < MaxAttempts)
            .OrderBy(m => m.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (pendingMessages.Count == 0) return;

        logger.LogDebug("Processing {Count} outbox messages", pendingMessages.Count);

        foreach (var message in pendingMessages)
            try
            {
                await PublishMessageAsync(message, scope.ServiceProvider);

                message.ProcessedAt = DateTimeOffset.UtcNow;
                message.LastError = null;
            }
            catch (Exception ex)
            {
                message.Attempts++;
                message.LastError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;

                logger.LogWarning(ex,
                    "Failed to publish outbox message {Id} (attempt {Attempt}/{Max})",
                    message.Id, message.Attempts, MaxAttempts);
            }

        await db.SaveChangesAsync(ct);

        var processed = pendingMessages.Count(m => m.ProcessedAt != null);
        if (processed > 0)
            logger.LogInformation("Processed {Processed}/{Total} outbox messages", processed, pendingMessages.Count);
    }

    private async Task PublishMessageAsync(OutboxMessage message, IServiceProvider scopedProvider)
    {
        switch (message.EventType)
        {
            case "PlayerUpdated":
                var playerUpdate = JsonSerializer.Deserialize<PlayerUpdatedMessage>(message.Payload);
                if (playerUpdate != null) await publisher.PublishPlayerUpdatedAsync(playerUpdate);
                break;

            case "GameEnded":
                await ProcessGameEndedAsync(message.Payload, scopedProvider);
                break;

            default:
                logger.LogWarning("Unknown outbox event type: {EventType}", message.EventType);
                break;
        }
    }

    private async Task ProcessGameEndedAsync(string payload, IServiceProvider scopedProvider)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var gameEndInfo = JsonSerializer.Deserialize<GameEndedPayload>(payload, options);

        if (gameEndInfo == null)
        {
            logger.LogError("Failed to deserialize GameEnded payload");
            return;
        }

        var archival = scopedProvider.GetRequiredService<IGameArchivalService>();

        await archival.EndGameAsync(
            gameEndInfo.RoomId,
            gameEndInfo.GameType,
            gameEndInfo.FinalState ?? new { },
            gameEndInfo.PlayerSeats,
            gameEndInfo.WinnerUserId,
            gameEndInfo.TotalPot,
            gameEndInfo.StartedAt,
            gameEndInfo.WinnerRanking);

        logger.LogInformation("Game archived via outbox: {RoomId} (Type: {GameType}, Winner: {Winner})",
            gameEndInfo.RoomId, gameEndInfo.GameType, gameEndInfo.WinnerUserId ?? "None");
    }

    private async Task CleanupOldMessagesAsync(CancellationToken ct)
    {
        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();

        var cutoff = DateTimeOffset.UtcNow - RetentionPeriod;

        var deleted = await db.OutboxMessages
            .Where(m => m.ProcessedAt != null && m.ProcessedAt < cutoff)
            .ExecuteDeleteAsync(ct);

        if (deleted > 0) logger.LogInformation("Cleaned up {Count} old outbox messages", deleted);

        var failedDeleted = await db.OutboxMessages
            .Where(m => m.Attempts >= MaxAttempts && m.CreatedAt < cutoff)
            .ExecuteDeleteAsync(ct);

        if (failedDeleted > 0)
            logger.LogWarning("Cleaned up {Count} failed outbox messages (exceeded max attempts)", failedDeleted);
    }
}