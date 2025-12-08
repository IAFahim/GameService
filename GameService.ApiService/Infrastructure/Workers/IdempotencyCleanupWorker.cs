using GameService.ServiceDefaults.Configuration;
using GameService.ServiceDefaults.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GameService.ApiService.Infrastructure.Workers;

public sealed class IdempotencyCleanupWorker(
    IServiceProvider serviceProvider,
    IOptions<GameServiceOptions> options,
    ILogger<IdempotencyCleanupWorker> logger) : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);
    private readonly int _retentionDays = options.Value.Economy.IdempotencyKeyRetentionDays;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("IdempotencyCleanupWorker started - retention period: {Days} days", _retentionDays);

        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupOldKeysAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during idempotency key cleanup");
            }

            await Task.Delay(CleanupInterval, stoppingToken);
        }
    }

    private async Task CleanupOldKeysAsync(CancellationToken ct)
    {
        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();

        var cutoffDate = DateTimeOffset.UtcNow.AddDays(-_retentionDays);

        var rowsUpdated = await db.WalletTransactions
            .Where(t => t.IdempotencyKey != null && t.CreatedAt < cutoffDate)
            .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.IdempotencyKey, (string?)null), ct);

        if (rowsUpdated > 0)
            logger.LogInformation("Cleared {Count} idempotency keys older than {Cutoff}", rowsUpdated, cutoffDate);
    }
}