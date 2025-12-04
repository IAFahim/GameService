using GameService.ServiceDefaults;
using GameService.ServiceDefaults.Configuration;
using GameService.ServiceDefaults.Data;
using GameService.ServiceDefaults.DTOs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GameService.ApiService.Features.Economy;

public interface IEconomyService
{
    Task<TransactionResult> ProcessTransactionAsync(string userId, long amount, string? referenceId = null,
        string? idempotencyKey = null);
}

public enum TransactionErrorType
{
    None,
    InvalidAmount,
    InsufficientFunds,
    ConcurrencyConflict,
    DuplicateTransaction,
    Unknown
}

public record TransactionResult(
    bool Success,
    long NewBalance,
    TransactionErrorType ErrorType = TransactionErrorType.None,
    string? ErrorMessage = null);

public class EconomyService(
    GameDbContext db,
    IGameEventPublisher publisher,
    IOptions<GameServiceOptions> options,
    ILogger<EconomyService> logger) : IEconomyService
{
    private readonly long _initialCoins = options.Value.Economy.InitialCoins;

    public async Task<TransactionResult> ProcessTransactionAsync(string userId, long amount, string? referenceId = null,
        string? idempotencyKey = null)
    {
        if (amount == 0)
            return new TransactionResult(false, 0, TransactionErrorType.InvalidAmount, "Amount cannot be zero");

        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            using var transaction = await db.Database.BeginTransactionAsync();
            try
            {
                if (!string.IsNullOrEmpty(idempotencyKey))
                {
                    var existing = await db.WalletTransactions
                        .AsNoTracking()
                        .FirstOrDefaultAsync(t => t.IdempotencyKey == idempotencyKey);

                    if (existing != null)
                    {
                        logger.LogWarning("Duplicate transaction attempt with key {Key} for user {UserId}",
                            idempotencyKey, userId);
                        return new TransactionResult(false, existing.BalanceAfter,
                            TransactionErrorType.DuplicateTransaction, "Transaction already processed");
                    }
                }

                var rows = await db.PlayerProfiles
                    .Where(p => p.UserId == userId && !p.IsDeleted)
                    .Where(p => amount >= 0 || p.Coins + amount >= 0)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(p => p.Coins, p => p.Coins + amount)
                        .SetProperty(p => p.Version, Guid.NewGuid()));

                long newBalance = 0;

                if (rows > 0)
                {
                    newBalance = await db.PlayerProfiles
                        .Where(p => p.UserId == userId)
                        .Select(p => p.Coins)
                        .FirstAsync();
                }
                else
                {
                    var profile = await db.PlayerProfiles
                        .AsNoTracking()
                        .FirstOrDefaultAsync(p => p.UserId == userId && !p.IsDeleted);

                    if (profile != null)
                        return new TransactionResult(false, profile.Coins, TransactionErrorType.InsufficientFunds,
                            "Insufficient funds");

                    var user = await db.Users.FindAsync(userId);
                    if (user is null)
                        return new TransactionResult(false, 0, TransactionErrorType.Unknown, "User account not found.");

                    var initialCoins = _initialCoins + amount;
                    if (initialCoins < 0)
                        return new TransactionResult(false, 0, TransactionErrorType.InsufficientFunds,
                            "Insufficient funds for initial operation");

                    var newProfile = new PlayerProfile { UserId = userId, Coins = initialCoins, User = user };
                    db.PlayerProfiles.Add(newProfile);
                    await db.SaveChangesAsync();
                    newBalance = initialCoins;
                }

                var txType = amount > 0 ? "Credit" : "Debit";
                var description = amount > 0 ? "Deposit/Win" : "Withdrawal/Entry Fee";

                var ledgerEntry = new WalletTransaction
                {
                    UserId = userId,
                    Amount = amount,
                    BalanceAfter = newBalance,
                    TransactionType = txType,
                    Description = description,
                    ReferenceId = referenceId ?? "System",
                    IdempotencyKey = idempotencyKey,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                db.WalletTransactions.Add(ledgerEntry);
                await db.SaveChangesAsync();

                await transaction.CommitAsync();

                // Publish event after commit - best effort with error logging
                // Event loss is acceptable as UI can refresh, but we log failures
                try
                {
                    await publisher.PublishPlayerUpdatedAsync(new PlayerUpdatedMessage(
                        userId,
                        newBalance,
                        null,
                        null));
                }
                catch (Exception pubEx)
                {
                    logger.LogWarning(pubEx, "Failed to publish player update event for user {UserId}. UI may be stale.", userId);
                }

                return new TransactionResult(true, newBalance);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Transaction failed for user {UserId}", userId);
                await transaction.RollbackAsync();
                return new TransactionResult(false, 0, TransactionErrorType.Unknown, "Transaction failed");
            }
        });
    }
}