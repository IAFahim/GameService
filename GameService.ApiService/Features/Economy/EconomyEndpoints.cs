using System.Security.Claims;
using System.Text.Json;
using GameService.ServiceDefaults.Configuration;
using GameService.ServiceDefaults.Data;
using GameService.ServiceDefaults.DTOs;
using GameService.ServiceDefaults.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GameService.ApiService.Features.Economy;

public static class EconomyEndpoints
{
    public static void MapEconomyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/game").RequireAuthorization();

        group.MapPost("/coins/transaction", ProcessTransaction);

        var publicGroup = app.MapGroup("/game/public");
        publicGroup.MapPost("/coins/credit", ProcessCreditTransaction);
        
        group.MapGet("/coins/history", GetTransactionHistory);

        group.MapPost("/daily-login", ClaimDailyLogin);
        group.MapPost("/daily-spin", ClaimDailySpin);

        group.MapPost("/{gameType}/bonus/welcome", ClaimGameWelcomeBonus);
        group.MapPost("/{gameType}/bonus/daily", ClaimGameDailyReward);
        group.MapGet("/{gameType}/config/economy", GetGameEconomyConfig);

        group.MapGet("/economy/config", GetGlobalEconomyConfig);
    }

    private static async Task<IResult> ProcessCreditTransaction(
        [FromBody] CreditTransactionRequest req,
        [FromHeader(Name = "X-Payment-Signature")] string signature,
        IEconomyService service,
        IOptions<GameServiceOptions> options,
        ILogger<EconomyService> logger)
    {
        if (string.IsNullOrWhiteSpace(signature))
        {
            logger.LogWarning("Credit attempt without signature for User {UserId}", req.UserId);
            return Results.Unauthorized();
        }

        var secret = options.Value.Economy.PaymentWebhookSecret;
        if (string.IsNullOrWhiteSpace(secret))
        {
            logger.LogError("Payment webhook secret not configured; rejecting credit request.");
            return Results.Problem("Payment webhook not configured", statusCode: StatusCodes.Status500InternalServerError);
        }

        var provided = signature.Trim();
        if (provided.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
            provided = provided["sha256=".Length..];

        byte[] providedBytes;
        try
        {
            providedBytes = Convert.FromHexString(provided);
        }
        catch
        {
            logger.LogWarning("Invalid payment signature format for User {UserId}", req.UserId);
            return Results.Unauthorized();
        }

        var payload = $"{req.UserId}:{req.Amount}:{req.ReferenceId}:{req.IdempotencyKey ?? ""}";
        using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(secret));
        var computedBytes = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(payload));

        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(computedBytes, providedBytes))
        {
            logger.LogWarning("Invalid payment signature for User {UserId}", req.UserId);
            return Results.Unauthorized();
        }

        if (req.Amount <= 0) return Results.BadRequest("Amount must be positive");
        if (string.IsNullOrEmpty(req.ReferenceId)) return Results.BadRequest("ReferenceId required");

        var result = await service.ProcessTransactionAsync(req.UserId, req.Amount, req.ReferenceId, req.IdempotencyKey);
        
        if (!result.Success)
        {
             if (result.ErrorType == TransactionErrorType.DuplicateTransaction)
                return Results.Conflict(result.ErrorMessage);
             return Results.BadRequest(result.ErrorMessage);
        }
        
        return Results.Ok(new { result.NewBalance });
    }

    public record CreditTransactionRequest(string UserId, long Amount, string ReferenceId, string? IdempotencyKey);

    private static async Task<IResult> GetGlobalEconomyConfig(
        GameDbContext db,
        IOptions<GameServiceOptions> options)
    {
        var setting = await db.GlobalSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == "Economy:InitialCoins");
        long val = options.Value.Economy.InitialCoins;
        
        if (setting != null && long.TryParse(setting.Value, out var parsed))
            val = parsed;
            
        return Results.Ok(new { InitialCoins = val });
    }

    private static async Task<IResult> ClaimGameWelcomeBonus(
        string gameType,
        HttpContext ctx,
        IEconomyService economy)
    {
        var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var result = await economy.ClaimGameWelcomeBonusAsync(userId, gameType);
        if (!result.Success) return Results.BadRequest(result.ErrorMessage);

        return Results.Ok(new { result.NewBalance });
    }

    private static async Task<IResult> ClaimGameDailyReward(
        string gameType,
        HttpContext ctx,
        IEconomyService economy)
    {
        var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var result = await economy.ClaimGameDailyRewardAsync(userId, gameType);
        if (!result.Success) return Results.BadRequest(result.ErrorMessage);

        return Results.Ok(new { result.NewBalance });
    }

    private static async Task<IResult> GetGameEconomyConfig(
        string gameType,
        IEconomyService economy)
    {
        var config = await economy.GetGameEconomyConfigAsync(gameType);
        return Results.Ok(config);
    }

    private static async Task<IResult> ClaimDailyLogin(
        HttpContext ctx,
        GameDbContext db,
        IEconomyService economy)
    {
        var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var settings = await db.GlobalSettings
            .Where(s => s.Key.StartsWith("Economy:DailyLogin"))
            .ToDictionaryAsync(s => s.Key, s => s.Value);

        if (!settings.TryGetValue("Economy:DailyLoginEnabled", out var enabled) || enabled != "true")
            return Results.BadRequest("Daily login rewards are disabled.");

        var profile = await db.PlayerProfiles.FirstOrDefaultAsync(p => p.UserId == userId);
        if (profile == null) return Results.NotFound("Profile not found");

        var now = DateTimeOffset.UtcNow;
        if (profile.LastDailyLogin.HasValue && profile.LastDailyLogin.Value.Date == now.Date)
            return Results.BadRequest("Already claimed today.");

        if (profile.LastDailyLogin.HasValue && profile.LastDailyLogin.Value.Date == now.AddDays(-1).Date)
        {
            profile.DailyLoginStreak++;
        }
        else
        {
            profile.DailyLoginStreak = 1;
        }

        RewardDto reward = new("Coin", "Coin", 100);
        if (settings.TryGetValue("Economy:DailyLoginConfig", out var json))
        {
            try
            {
                var rewards = JsonSerializer.Deserialize<List<RewardDto>>(json);
                if (rewards != null && rewards.Count > 0)
                {
                    var dayIndex = (profile.DailyLoginStreak - 1) % rewards.Count;
                    reward = rewards[dayIndex];
                }
            }
            catch {
            }
        }
        else if (settings.TryGetValue("Economy:DailyLoginRewards", out var oldJson))
        {
            try
            {
                var amounts = JsonSerializer.Deserialize<List<long>>(oldJson);
                if (amounts != null && amounts.Count > 0)
                {
                    var dayIndex = (profile.DailyLoginStreak - 1) % amounts.Count;
                    reward = new RewardDto("Coin", "Coin", amounts[dayIndex]);
                }
            }
            catch {
            }
        }

        long newBalance = profile.Coins;
        if (reward.Type == "Coin")
        {
            var result = await economy.ProcessTransactionAsync(userId, reward.Amount, "DAILY_LOGIN", $"LOGIN:{now:yyyyMMdd}");
            if (!result.Success) return Results.BadRequest(result.ErrorMessage);
            newBalance = result.NewBalance;
        }
        else
        {
            var inventory = new Dictionary<string, int>();
            if (!string.IsNullOrEmpty(profile.InventoryJson))
            {
                try { inventory = JsonSerializer.Deserialize<Dictionary<string, int>>(profile.InventoryJson) ?? new(); } catch {}
            }
            
            if (!inventory.ContainsKey(reward.Reference)) inventory[reward.Reference] = 0;
            inventory[reward.Reference] += (int)reward.Amount;
            
            profile.InventoryJson = JsonSerializer.Serialize(inventory);

            db.WalletTransactions.Add(new WalletTransaction
            {
                UserId = userId,
                Amount = reward.Amount,
                BalanceAfter = inventory[reward.Reference],
                TransactionType = "DAILY_LOGIN",
                Description = $"Daily Login Reward: {reward.Reference}",
                ReferenceId = $"LOGIN:{now:yyyyMMdd}",
                Currency = reward.Reference,
                CreatedAt = now
            });
        }

        profile.LastDailyLogin = now;
        await db.SaveChangesAsync();

        return Results.Ok(new { Reward = reward, NewBalance = newBalance, Streak = profile.DailyLoginStreak });
    }

    private static async Task<IResult> ClaimDailySpin(
        HttpContext ctx,
        GameDbContext db,
        IEconomyService economy)
    {
        var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var settings = await db.GlobalSettings
            .Where(s => s.Key.StartsWith("Economy:DailySpin"))
            .ToDictionaryAsync(s => s.Key, s => s.Value);

        if (!settings.TryGetValue("Economy:DailySpinEnabled", out var enabled) || enabled != "true")
            return Results.BadRequest("Daily spin is disabled.");

        var profile = await db.PlayerProfiles.FirstOrDefaultAsync(p => p.UserId == userId);
        if (profile == null) return Results.NotFound("Profile not found");

        if (profile.LastDailySpin.HasValue && profile.LastDailySpin.Value.Date == DateTimeOffset.UtcNow.Date)
            return Results.BadRequest("Already spun today.");

        RewardDto reward = new("Coin", "Coin", 50);
        if (settings.TryGetValue("Economy:DailySpinConfig", out var json))
        {
            try
            {
                var rewards = JsonSerializer.Deserialize<List<SpinRewardConfig>>(json);
                if (rewards != null && rewards.Count > 0)
                {
                    var totalWeight = rewards.Sum(r => r.Weight);
                    var roll = Random.Shared.Next(0, totalWeight);
                    var current = 0;
                    foreach (var r in rewards)
                    {
                        current += r.Weight;
                        if (roll < current)
                        {
                            reward = r.Reward;
                            break;
                        }
                    }
                }
            }
            catch {
            }
        }

        long newBalance = profile.Coins;
        var now = DateTimeOffset.UtcNow;

        if (reward.Type == "Coin")
        {
            var result = await economy.ProcessTransactionAsync(userId, reward.Amount, "DAILY_SPIN", $"SPIN:{now:yyyyMMdd}");
            if (!result.Success) return Results.BadRequest(result.ErrorMessage);
            newBalance = result.NewBalance;
        }
        else
        {
            var inventory = new Dictionary<string, int>();
            if (!string.IsNullOrEmpty(profile.InventoryJson))
            {
                try { inventory = JsonSerializer.Deserialize<Dictionary<string, int>>(profile.InventoryJson) ?? new(); } catch {}
            }
            
            if (!inventory.ContainsKey(reward.Reference)) inventory[reward.Reference] = 0;
            inventory[reward.Reference] += (int)reward.Amount;
            
            profile.InventoryJson = JsonSerializer.Serialize(inventory);
            
            db.WalletTransactions.Add(new WalletTransaction
            {
                UserId = userId,
                Amount = reward.Amount,
                BalanceAfter = inventory[reward.Reference],
                TransactionType = "DAILY_SPIN",
                Description = $"Daily Spin Reward: {reward.Reference}",
                ReferenceId = $"SPIN:{now:yyyyMMdd}",
                Currency = reward.Reference,
                CreatedAt = now
            });
        }

        profile.LastDailySpin = now;
        await db.SaveChangesAsync();

        return Results.Ok(new { Reward = reward, NewBalance = newBalance });
    }

    private record SpinRewardConfig(RewardDto Reward, int Weight);
    private record SpinReward(long Amount, int Weight);

    private static async Task<IResult> ProcessTransaction(
        [FromBody] UpdateCoinRequest req,
        HttpContext ctx,
        IEconomyService service)
    {
        var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        if (!InputValidator.IsValidCoinAmount(req.Amount))
            return Results.BadRequest("Invalid amount");

        if (!InputValidator.IsValidReferenceId(req.ReferenceId))
            return Results.BadRequest("Invalid reference ID format");

        if (!InputValidator.IsValidIdempotencyKey(req.IdempotencyKey))
            return Results.BadRequest("Invalid idempotency key format");

        if (req.Amount > 0)
        {
            return Results.BadRequest("Users cannot credit coins directly. Use the payment webhook endpoint.");
        }

        var result = await service.ProcessTransactionAsync(userId, req.Amount, req.ReferenceId, req.IdempotencyKey);

        if (!result.Success)
        {
            if (result.ErrorType == TransactionErrorType.ConcurrencyConflict)
                return Results.Conflict(result.ErrorMessage);

            if (result.ErrorType == TransactionErrorType.DuplicateTransaction)
                return Results.Conflict(result.ErrorMessage);

            return Results.BadRequest(result.ErrorMessage);
        }

        return Results.Ok(new { result.NewBalance });
    }

    private static async Task<IResult> GetTransactionHistory(
        HttpContext ctx,
        GameDbContext db,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);

        var query = db.WalletTransactions
            .AsNoTracking()
            .Where(t => t.UserId == userId);

        var totalCount = await query.CountAsync();

        var items = await query
            .OrderByDescending(t => t.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new WalletTransactionDto(
                t.Id,
                t.Amount,
                t.BalanceAfter,
                t.TransactionType,
                t.Description,
                t.ReferenceId,
                t.CreatedAt))
            .ToListAsync();

        return Results.Ok(new PagedResult<WalletTransactionDto>(items, totalCount, page, pageSize));
    }
}