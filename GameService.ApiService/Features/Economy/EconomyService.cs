using GameService.ServiceDefaults;
using GameService.ServiceDefaults.Data;
using GameService.ServiceDefaults.DTOs;
using Microsoft.EntityFrameworkCore;

namespace GameService.ApiService.Features.Economy;

public interface IEconomyService
{
    Task<TransactionResult> ProcessTransactionAsync(string userId, long amount);
}

public enum TransactionErrorType { None, InvalidAmount, InsufficientFunds, ConcurrencyConflict, Unknown }

public record TransactionResult(bool Success, long NewBalance, TransactionErrorType ErrorType = TransactionErrorType.None, string? ErrorMessage = null);

public class EconomyService(GameDbContext db, IGameEventPublisher publisher) : IEconomyService
{
    private const int MaxRetries = 3;
    
    public async Task<TransactionResult> ProcessTransactionAsync(string userId, long amount)
    {
        return await ProcessTransactionInternalAsync(userId, amount, retryCount: 0);
    }
    
    private async Task<TransactionResult> ProcessTransactionInternalAsync(string userId, long amount, int retryCount)
    {
        if (amount == 0) return new TransactionResult(false, 0, TransactionErrorType.InvalidAmount, "Amount cannot be zero");

        var strategy = db.Database.CreateExecutionStrategy();

        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                using var transaction = await db.Database.BeginTransactionAsync();
                try
                {
                    var rows = await db.PlayerProfiles
                        .Where(p => p.UserId == userId)
                        .Where(p => amount >= 0 || (p.Coins + amount) >= 0)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(p => p.Coins, p => p.Coins + amount)
                            .SetProperty(p => p.Version, Guid.NewGuid()));
                    
                    if (rows == 0)
                    {
                        var userExists = await db.PlayerProfiles.AnyAsync(p => p.UserId == userId);
                        if (!userExists)
                        {
                            var user = await db.Users.FindAsync(userId);
                            if (user is null) return new TransactionResult(false, 0, TransactionErrorType.Unknown, "User account not found.");
                            
                            var newProfile = new PlayerProfile { UserId = userId, Coins = 100 + amount, User = user };
                            db.PlayerProfiles.Add(newProfile);
                            await db.SaveChangesAsync();
                            
                            await transaction.CommitAsync();
                            return new TransactionResult(true, newProfile.Coins, TransactionErrorType.None);
                        }
                        else
                        {
                            var currentCoins = await db.PlayerProfiles
                                .Where(p => p.UserId == userId)
                                .Select(p => p.Coins)
                                .SingleAsync();

                            return new TransactionResult(false, currentCoins, TransactionErrorType.InsufficientFunds, "Insufficient funds");
                        }
                    }

                    await transaction.CommitAsync();
                    
                    var newBalance = await db.PlayerProfiles.Where(p => p.UserId == userId).Select(p => p.Coins).FirstAsync();

                    var message = new PlayerUpdatedMessage(
                        userId, 
                        newBalance, 
                        null, 
                        null,
                        PlayerChangeType.Updated,
                        0);

                    await publisher.PublishPlayerUpdatedAsync(message);

                    return new TransactionResult(true, newBalance, TransactionErrorType.None);
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    return new TransactionResult(false, 0, TransactionErrorType.Unknown, ex.Message);
                }
            });
        }
        catch (DbUpdateException) when (retryCount < MaxRetries)
        {
            db.ChangeTracker.Clear();
            return await ProcessTransactionInternalAsync(userId, amount, retryCount + 1);
        }
    }
}