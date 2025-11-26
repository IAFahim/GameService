using System.Text.Json;
using GameService.ApiService;
using GameService.ServiceDefaults.Data;
using GameService.ServiceDefaults.DTOs;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace GameService.ApiService.Features.Economy;

public interface IEconomyService
{
    Task<TransactionResult> ProcessTransactionAsync(string userId, long amount);
}

public record TransactionResult(bool Success, long NewBalance, string? Error = null);

public class EconomyService(GameDbContext db, IConnectionMultiplexer redis) : IEconomyService
{
    public async Task<TransactionResult> ProcessTransactionAsync(string userId, long amount)
    {
        if (amount == 0) return new TransactionResult(false, 0, "Amount cannot be zero");

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
                    profile = new PlayerProfile { UserId = userId, Coins = 100 };
                    db.PlayerProfiles.Add(profile);
                }

                if (amount < 0 && (profile.Coins + amount < 0))
                {
                    return new TransactionResult(false, 0, "Insufficient funds");
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

                var json = JsonSerializer.Serialize(message, GameJsonContext.Default.PlayerUpdatedMessage);
                await redis.GetSubscriber().PublishAsync(RedisChannel.Literal("player_updates"), json);

                return new TransactionResult(true, profile.Coins);
            }
            catch (DbUpdateConcurrencyException)
            {
                return new TransactionResult(false, 0, "Transaction failed due to concurrent modification. Please retry.");
            }
        });
    }
}