using GameService.ApiService.Features.Common;
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
    public async Task<TransactionResult> ProcessTransactionAsync(string userId, long amount)
    {
        if (amount == 0) return new TransactionResult(false, 0, TransactionErrorType.InvalidAmount, "Amount cannot be zero");

        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            using var transaction = await db.Database.BeginTransactionAsync();
            try
            {
                var profile = await db.PlayerProfiles
                    .Include(p => p.User)
                    .FirstOrDefaultAsync(p => p.UserId == userId);

                if (profile is null)
                {
                    // Fix: Load user to ensure we have data for the event
                    var user = await db.Users.FindAsync(userId);
                    profile = new PlayerProfile { UserId = userId, Coins = 100, User = user! };
                    db.PlayerProfiles.Add(profile);
                }

                // Check for insufficient funds
                if (amount < 0 && (profile.Coins + amount < 0))
                {
                    return new TransactionResult(false, profile.Coins, TransactionErrorType.InsufficientFunds, "Insufficient funds");
                }

                profile.Coins += amount;
                profile.Version = Guid.NewGuid();

                await db.SaveChangesAsync();
                await transaction.CommitAsync();

                var message = new PlayerUpdatedMessage(
                    profile.UserId, 
                    profile.Coins, 
                    profile.User?.UserName ?? "Unknown", 
                    profile.User?.Email ?? "Unknown");

                await publisher.PublishPlayerUpdatedAsync(message);

                return new TransactionResult(true, profile.Coins, TransactionErrorType.None);
            }
            catch (DbUpdateConcurrencyException)
            {
                return new TransactionResult(false, 0, TransactionErrorType.ConcurrencyConflict, "Transaction failed due to concurrent modification. Please retry.");
            }
        });
    }
}