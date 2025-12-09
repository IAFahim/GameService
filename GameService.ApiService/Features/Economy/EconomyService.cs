using System.Data;
using System.Text.Json;
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
    
    Task<EntryFeeReservation> ReserveEntryFeeAsync(string userId, long entryFee, string roomId);
    
    Task CommitEntryFeeAsync(EntryFeeReservation reservation);

    
    Task RefundEntryFeeAsync(EntryFeeReservation reservation);

    
    Task<TransactionResult> AwardWinningsAsync(string userId, long amount, string roomId);

    
    Task<GamePayoutResult> ProcessGamePayoutsAsync(string roomId, string gameType, long totalPot,
        IReadOnlyDictionary<string, int> playerSeats, string? winnerUserId,
        IReadOnlyList<string>? winnerRanking = null);

    Task<TransactionResult> ClaimGameWelcomeBonusAsync(string userId, string gameType);
    Task<TransactionResult> ClaimGameDailyRewardAsync(string userId, string gameType);
    Task<JsonElement> GetGameEconomyConfigAsync(string gameType);
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


public record EntryFeeReservation(
    bool Success,
    string UserId,
    long Amount,
    string RoomId,
    string? ReservationId,
    long NewBalance,
    string? ErrorMessage = null);


public record GamePayoutResult(
    bool Success,
    IReadOnlyDictionary<string, long> Payouts,
    string? ErrorMessage = null);

public class EconomyService(
    GameDbContext db,
    IGameEventPublisher publisher,
    IOptions<GameServiceOptions> options,
    ILogger<EconomyService> logger) : IEconomyService
{
    private const int MaxRetryAttempts = 3;
    private readonly long _initialCoins = options.Value.Economy.InitialCoins;

    public async Task<TransactionResult> ProcessTransactionAsync(string userId, long amount, string? referenceId = null,
        string? idempotencyKey = null)
    {
        if (amount == 0)
            return new TransactionResult(false, 0, TransactionErrorType.InvalidAmount, "Amount cannot be zero");

        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            for (var attempt = 0; attempt < MaxRetryAttempts; attempt++)
            {
                using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);
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

                    var profile = await db.PlayerProfiles
                        .FromSqlRaw(
                            "SELECT * FROM \"PlayerProfiles\" WHERE \"UserId\" = {0} AND \"IsDeleted\" = false FOR UPDATE",
                            userId)
                        .FirstOrDefaultAsync();

                    long newBalance;

                    if (profile != null)
                    {
                        if (amount < 0 && profile.Coins + amount < 0)
                            return new TransactionResult(false, profile.Coins,
                                TransactionErrorType.InsufficientFunds, "Insufficient funds");

                        var currentVersion = profile.Version;
                        profile.Coins += amount;
                        profile.Version = Guid.NewGuid();

                        var rows = await db.SaveChangesAsync();
                        if (rows == 0)
                        {
                            await transaction.RollbackAsync();
                            if (attempt < MaxRetryAttempts - 1)
                            {
                                await Task.Delay(Random.Shared.Next(10, 50));
                                continue;
                            }

                            return new TransactionResult(false, 0,
                                TransactionErrorType.ConcurrencyConflict, "Concurrent modification detected");
                        }

                        newBalance = profile.Coins;
                    }
                    else
                    {
                        var user = await db.Users.FindAsync(userId);
                        if (user is null)
                            return new TransactionResult(false, 0, TransactionErrorType.Unknown,
                                "User account not found.");

                        // Check for dynamic initial coins setting
                        var dynamicInitial = await db.GlobalSettings
                            .AsNoTracking()
                            .Where(s => s.Key == "Economy:InitialCoins")
                            .Select(s => s.Value)
                            .FirstOrDefaultAsync();

                        long startCoins = _initialCoins;
                        if (long.TryParse(dynamicInitial, out var parsed)) startCoins = parsed;

                        var initialCoins = startCoins + amount;
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

                    var outboxMessage = new OutboxMessage
                    {
                        EventType = "PlayerUpdated",
                        Payload = JsonSerializer.Serialize(new PlayerUpdatedMessage(
                            userId, newBalance, null, null)),
                        CreatedAt = DateTimeOffset.UtcNow
                    };
                    db.OutboxMessages.Add(outboxMessage);

                    await db.SaveChangesAsync();
                    await transaction.CommitAsync();

                    try
                    {
                        await publisher.PublishPlayerUpdatedAsync(new PlayerUpdatedMessage(
                            userId, newBalance, null, null));

                        outboxMessage.ProcessedAt = DateTimeOffset.UtcNow;
                        await db.SaveChangesAsync();
                    }
                    catch (Exception pubEx)
                    {
                        logger.LogDebug(pubEx, "Immediate publish failed for user {UserId}, outbox will retry", userId);
                    }

                    return new TransactionResult(true, newBalance);
                }
                catch (DbUpdateConcurrencyException)
                {
                    await transaction.RollbackAsync();
                    if (attempt < MaxRetryAttempts - 1)
                    {
                        await Task.Delay(Random.Shared.Next(10, 50));
                        continue;
                    }

                    return new TransactionResult(false, 0,
                        TransactionErrorType.ConcurrencyConflict, "Concurrent modification detected");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Transaction failed for user {UserId}", userId);
                    await transaction.RollbackAsync();
                    return new TransactionResult(false, 0, TransactionErrorType.Unknown, "Transaction failed");
                }
            }

            return new TransactionResult(false, 0, TransactionErrorType.ConcurrencyConflict, "Max retries exceeded");
        });
    }

    public async Task<EntryFeeReservation> ReserveEntryFeeAsync(string userId, long entryFee, string roomId)
    {
        if (entryFee <= 0)
            return new EntryFeeReservation(true, userId, 0, roomId, null, 0);

        var reservationId = $"reserve:{roomId}:{userId}:{Guid.NewGuid():N}";
        var result = await ProcessTransactionAsync(userId, -entryFee, $"ROOM:{roomId}:ENTRY_RESERVE", reservationId);

        if (!result.Success)
            return new EntryFeeReservation(false, userId, entryFee, roomId, null, result.NewBalance,
                result.ErrorMessage ?? "Insufficient funds");

        return new EntryFeeReservation(true, userId, entryFee, roomId, reservationId, result.NewBalance);
    }

    public async Task CommitEntryFeeAsync(EntryFeeReservation reservation)
    {
        if (!reservation.Success || reservation.Amount <= 0) return;

        await db.WalletTransactions
            .Where(t => t.IdempotencyKey == reservation.ReservationId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.Description, "Entry Fee (Confirmed)")
                .SetProperty(t => t.ReferenceId, $"ROOM:{reservation.RoomId}:ENTRY"));

        logger.LogInformation("Entry fee committed: User={UserId}, Room={RoomId}, Amount={Amount}",
            reservation.UserId, reservation.RoomId, reservation.Amount);
    }

    public async Task RefundEntryFeeAsync(EntryFeeReservation reservation)
    {
        if (!reservation.Success || reservation.Amount <= 0) return;

        var refundKey = $"refund:{reservation.ReservationId}";
        var result = await ProcessTransactionAsync(
            reservation.UserId,
            reservation.Amount, $"ROOM:{reservation.RoomId}:ENTRY_REFUND",
            refundKey);

        if (result.Success)
            logger.LogInformation("Entry fee refunded: User={UserId}, Room={RoomId}, Amount={Amount}",
                reservation.UserId, reservation.RoomId, reservation.Amount);
        else
            logger.LogError("Failed to refund entry fee: User={UserId}, Room={RoomId}, Amount={Amount}, Error={Error}",
                reservation.UserId, reservation.RoomId, reservation.Amount, result.ErrorMessage);
    }

    public async Task<TransactionResult> AwardWinningsAsync(string userId, long amount, string roomId)
    {
        if (amount <= 0) return new TransactionResult(true, 0);

        var idempotencyKey = $"win:{roomId}:{userId}";
        return await ProcessTransactionAsync(userId, amount, $"ROOM:{roomId}:WIN", idempotencyKey);
    }

    public async Task<GamePayoutResult> ProcessGamePayoutsAsync(
        string roomId,
        string gameType,
        long totalPot,
        IReadOnlyDictionary<string, int> playerSeats,
        string? winnerUserId,
        IReadOnlyList<string>? winnerRanking = null)
    {
        if (totalPot <= 0)
            return new GamePayoutResult(true, new Dictionary<string, long>());

        var payouts = new Dictionary<string, long>();
        var playerCount = playerSeats.Count;

        var rake = (long)(totalPot * 0.03);
        var prizePool = totalPot - rake;

        try
        {
            if (winnerRanking != null && winnerRanking.Count > 0)
            {
                payouts = CalculateRankedPayouts(winnerRanking, prizePool);
            }
            else if (!string.IsNullOrEmpty(winnerUserId))
            {
                payouts[winnerUserId] = prizePool;
            }
            else
            {
                var refundPerPlayer = prizePool / playerCount;
                foreach (var userId in playerSeats.Keys) payouts[userId] = refundPerPlayer;
            }

            foreach (var (userId, amount) in payouts)
            {
                var result = await AwardWinningsAsync(userId, amount, roomId);
                if (!result.Success)
                    logger.LogError("Failed to pay {Amount} to {UserId} for room {RoomId}: {Error}",
                        amount, userId, roomId, result.ErrorMessage);
            }

            logger.LogInformation(
                "Game payouts processed: Room={RoomId}, Type={GameType}, TotalPot={Pot}, Rake={Rake}, Payouts={Count}",
                roomId, gameType, totalPot, rake, payouts.Count);

            return new GamePayoutResult(true, payouts);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process game payouts for room {RoomId}", roomId);
            return new GamePayoutResult(false, payouts, ex.Message);
        }
    }

    private static Dictionary<string, long> CalculateRankedPayouts(IReadOnlyList<string> ranking, long prizePool)
    {
        var payouts = new Dictionary<string, long>();

        var payoutPercentages = ranking.Count switch
        {
            1 => new[] { 1.0 },
            2 => new[] { 0.7, 0.3 },
            3 => new[] { 0.5, 0.3, 0.2 },
            4 => new[] { 0.4, 0.3, 0.2, 0.1 },
            _ => CalculatePayoutPercentages(ranking.Count)
        };

        for (var i = 0; i < ranking.Count && i < payoutPercentages.Length; i++)
        {
            var payout = (long)(prizePool * payoutPercentages[i]);
            if (payout > 0) payouts[ranking[i]] = payout;
        }

        return payouts;
    }

    private static double[] CalculatePayoutPercentages(int playerCount)
    {
        var paidPositions = Math.Max(1, playerCount / 2);
        var percentages = new double[paidPositions];

        double total = 0;
        for (var i = 0; i < paidPositions; i++)
        {
            percentages[i] = 1.0 / (i + 1);
            total += percentages[i];
        }

        for (var i = 0; i < paidPositions; i++) percentages[i] /= total;

        return percentages;
    }

    public async Task<TransactionResult> ClaimGameWelcomeBonusAsync(string userId, string gameType)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);
            try
            {
                var progression = await db.PlayerGameProgressions
                    .FirstOrDefaultAsync(p => p.UserId == userId && p.GameType == gameType);

                if (progression == null)
                {
                    progression = new PlayerGameProgression
                    {
                        UserId = userId,
                        GameType = gameType,
                        DailyLoginStreak = 0,
                        LastDailyLogin = DateTimeOffset.MinValue,
                        HasClaimedWelcomeBonus = false
                    };
                    db.PlayerGameProgressions.Add(progression);
                }

                if (progression.HasClaimedWelcomeBonus)
                {
                    return new TransactionResult(false, 0, TransactionErrorType.DuplicateTransaction, "Welcome bonus already claimed");
                }

                var configJson = await db.GlobalSettings
                    .Where(s => s.Key == $"Game:{gameType}:Economy")
                    .Select(s => s.Value)
                    .FirstOrDefaultAsync();

                long bonusAmount = 0;
                if (!string.IsNullOrEmpty(configJson))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(configJson);
                        if (doc.RootElement.TryGetProperty("WelcomeBonus", out var bonusProp))
                        {
                            bonusAmount = bonusProp.GetInt64();
                        }
                    }
                    catch { /* ignore */ }
                }

                if (bonusAmount <= 0) bonusAmount = 100; // Default

                progression.HasClaimedWelcomeBonus = true;

                // Credit logic
                var profile = await db.PlayerProfiles
                    .FromSqlRaw("SELECT * FROM \"PlayerProfiles\" WHERE \"UserId\" = {0} AND \"IsDeleted\" = false FOR UPDATE", userId)
                    .FirstOrDefaultAsync();

                long newBalance;
                if (profile != null)
                {
                    profile.Coins += bonusAmount;
                    profile.Version = Guid.NewGuid();
                    newBalance = profile.Coins;
                }
                else
                {
                    // Should not happen for existing user but handle it
                    var user = await db.Users.FindAsync(userId);
                    if (user == null) return new TransactionResult(false, 0, TransactionErrorType.Unknown, "User not found");
                    
                    newBalance = _initialCoins + bonusAmount;
                    profile = new PlayerProfile { UserId = userId, Coins = newBalance, User = user };
                    db.PlayerProfiles.Add(profile);
                }

                var ledgerEntry = new WalletTransaction
                {
                    UserId = userId,
                    Amount = bonusAmount,
                    BalanceAfter = newBalance,
                    TransactionType = "Credit",
                    Description = $"Welcome Bonus: {gameType}",
                    ReferenceId = $"BONUS:{gameType}:WELCOME",
                    CreatedAt = DateTimeOffset.UtcNow
                };
                db.WalletTransactions.Add(ledgerEntry);

                var outboxMessage = new OutboxMessage
                {
                    EventType = "PlayerUpdated",
                    Payload = JsonSerializer.Serialize(new PlayerUpdatedMessage(userId, newBalance, null, null)),
                    CreatedAt = DateTimeOffset.UtcNow
                };
                db.OutboxMessages.Add(outboxMessage);

                await db.SaveChangesAsync();
                await transaction.CommitAsync();

                _ = Task.Run(() => publisher.PublishPlayerUpdatedAsync(new PlayerUpdatedMessage(userId, newBalance, null, null)));

                return new TransactionResult(true, newBalance);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                logger.LogError(ex, "Error claiming welcome bonus");
                return new TransactionResult(false, 0, TransactionErrorType.Unknown, "Error claiming bonus");
            }
        });
    }

    public async Task<TransactionResult> ClaimGameDailyRewardAsync(string userId, string gameType)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);
            try
            {
                var progression = await db.PlayerGameProgressions
                    .FirstOrDefaultAsync(p => p.UserId == userId && p.GameType == gameType);

                if (progression == null)
                {
                    progression = new PlayerGameProgression
                    {
                        UserId = userId,
                        GameType = gameType,
                        DailyLoginStreak = 0,
                        LastDailyLogin = DateTimeOffset.MinValue,
                        HasClaimedWelcomeBonus = false
                    };
                    db.PlayerGameProgressions.Add(progression);
                }

                var now = DateTimeOffset.UtcNow;
                var lastLogin = progression.LastDailyLogin;
                var daysDiff = (now.Date - lastLogin.Date).Days;

                if (daysDiff == 0)
                {
                    return new TransactionResult(false, 0, TransactionErrorType.DuplicateTransaction, "Daily reward already claimed today");
                }

                // Fetch reward config
                var rewardsJson = await db.GlobalSettings
                    .Where(s => s.Key == $"Game:{gameType}:DailyRewards")
                    .Select(s => s.Value)
                    .FirstOrDefaultAsync();

                var rewardsMap = new Dictionary<int, long>();
                if (!string.IsNullOrEmpty(rewardsJson))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(rewardsJson);
                        if (doc.RootElement.TryGetProperty("Rewards", out var rewardsProp) && rewardsProp.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var reward in rewardsProp.EnumerateArray())
                            {
                                if (reward.TryGetProperty("Day", out var dayProp) && reward.TryGetProperty("Amount", out var amountProp))
                                {
                                    rewardsMap[dayProp.GetInt32()] = amountProp.GetInt64();
                                }
                            }
                        }
                    }
                    catch { /* ignore */ }
                }

                // FIX: Correctly determine max day and loop logic
                int maxDay = rewardsMap.Keys.Count > 0 ? rewardsMap.Keys.Max() : 7;

                if (daysDiff == 1)
                {
                    progression.DailyLoginStreak++;
                    if (progression.DailyLoginStreak > maxDay) 
                    {
                        progression.DailyLoginStreak = 1; // Loop back to day 1
                    }
                }
                else
                {
                    progression.DailyLoginStreak = 1; // Reset to day 1 if missed a day
                }

                progression.LastDailyLogin = now;

                long rewardAmount = rewardsMap.TryGetValue(progression.DailyLoginStreak, out var amt) ? amt : 50;

                // Credit logic
                var profile = await db.PlayerProfiles
                    .FromSqlRaw("SELECT * FROM \"PlayerProfiles\" WHERE \"UserId\" = {0} AND \"IsDeleted\" = false FOR UPDATE", userId)
                    .FirstOrDefaultAsync();

                long newBalance;
                if (profile != null)
                {
                    profile.Coins += rewardAmount;
                    profile.Version = Guid.NewGuid();
                    newBalance = profile.Coins;
                }
                else
                {
                    var user = await db.Users.FindAsync(userId);
                    if (user == null) return new TransactionResult(false, 0, TransactionErrorType.Unknown, "User not found");
                    
                    newBalance = _initialCoins + rewardAmount;
                    profile = new PlayerProfile { UserId = userId, Coins = newBalance, User = user };
                    db.PlayerProfiles.Add(profile);
                }

                var ledgerEntry = new WalletTransaction
                {
                    UserId = userId,
                    Amount = rewardAmount,
                    BalanceAfter = newBalance,
                    TransactionType = "Credit",
                    Description = $"Daily Reward: {gameType} Day {progression.DailyLoginStreak}",
                    ReferenceId = $"BONUS:{gameType}:DAILY:{now:yyyyMMdd}",
                    CreatedAt = now
                };
                db.WalletTransactions.Add(ledgerEntry);

                var outboxMessage = new OutboxMessage
                {
                    EventType = "PlayerUpdated",
                    Payload = JsonSerializer.Serialize(new PlayerUpdatedMessage(userId, newBalance, null, null)),
                    CreatedAt = now
                };
                db.OutboxMessages.Add(outboxMessage);

                await db.SaveChangesAsync();
                await transaction.CommitAsync();

                _ = Task.Run(() => publisher.PublishPlayerUpdatedAsync(new PlayerUpdatedMessage(userId, newBalance, null, null)));

                return new TransactionResult(true, newBalance);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                logger.LogError(ex, "Error claiming daily reward");
                return new TransactionResult(false, 0, TransactionErrorType.Unknown, "Error claiming reward");
            }
        });
    }

    public async Task<JsonElement> GetGameEconomyConfigAsync(string gameType)
    {
        var economyJson = await db.GlobalSettings
            .Where(s => s.Key == $"Game:{gameType}:Economy")
            .Select(s => s.Value)
            .FirstOrDefaultAsync();

        var dailyRewardsJson = await db.GlobalSettings
            .Where(s => s.Key == $"Game:{gameType}:DailyRewards")
            .Select(s => s.Value)
            .FirstOrDefaultAsync();

        var spinWheelJson = await db.GlobalSettings
            .Where(s => s.Key == $"Game:{gameType}:SpinWheel")
            .Select(s => s.Value)
            .FirstOrDefaultAsync();

        var result = new Dictionary<string, object>();
        
        if (!string.IsNullOrEmpty(economyJson))
        {
            try { result["Economy"] = JsonDocument.Parse(economyJson).RootElement; } catch {}
        }
        
        if (!string.IsNullOrEmpty(dailyRewardsJson))
        {
            try { result["DailyRewards"] = JsonDocument.Parse(dailyRewardsJson).RootElement; } catch {}
        }

        if (!string.IsNullOrEmpty(spinWheelJson))
        {
            try { result["SpinWheel"] = JsonDocument.Parse(spinWheelJson).RootElement; } catch {}
        }

        return JsonSerializer.SerializeToElement(result);
    }
}