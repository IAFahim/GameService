using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System.Security.Claims;

namespace GameService.ServiceDefaults.Security;

/// <summary>
///     Middleware for token revocation (blacklisting).
///     When a user is banned, their JWT is added to Redis with TTL matching token expiry.
///     All requests are checked against this blacklist.
/// </summary>
public sealed class TokenRevocationMiddleware
{
    private const string TokenBlacklistPrefix = "revoked:jti:";
    private readonly ILogger<TokenRevocationMiddleware> _logger;
    private readonly RequestDelegate _next;
    private readonly IConnectionMultiplexer _redis;

    public TokenRevocationMiddleware(
        RequestDelegate next,
        IConnectionMultiplexer redis,
        ILogger<TokenRevocationMiddleware> logger)
    {
        _next = next;
        _redis = redis;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var jti = context.User.FindFirstValue("jti");
            
            if (!string.IsNullOrEmpty(jti))
            {
                var db = _redis.GetDatabase();
                var key = $"{TokenBlacklistPrefix}{jti}";
                
                if (await db.KeyExistsAsync(key))
                {
                    _logger.LogWarning("Revoked token used: jti={Jti}", jti);
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsync("Token has been revoked");
                    return;
                }
            }
        }

        await _next(context);
    }
}

/// <summary>
///     Service for managing token revocation
/// </summary>
public interface ITokenRevocationService
{
    /// <summary>
    ///     Revoke a specific token by its JTI claim
    /// </summary>
    Task RevokeTokenAsync(string jti, TimeSpan? ttl = null);

    /// <summary>
    ///     Revoke all tokens for a user by storing their user ID
    ///     Tokens issued before this time will be rejected
    /// </summary>
    Task RevokeAllUserTokensAsync(string userId);

    /// <summary>
    ///     Check if a token is revoked
    /// </summary>
    Task<bool> IsTokenRevokedAsync(string jti);
}

public sealed class RedisTokenRevocationService : ITokenRevocationService
{
    private const string TokenBlacklistPrefix = "revoked:jti:";
    private const string UserRevocationPrefix = "revoked:user:";
    private readonly IDatabase _db;
    private readonly ILogger<RedisTokenRevocationService> _logger;

    public RedisTokenRevocationService(
        IConnectionMultiplexer redis,
        ILogger<RedisTokenRevocationService> logger)
    {
        _db = redis.GetDatabase();
        _logger = logger;
    }

    public async Task RevokeTokenAsync(string jti, TimeSpan? ttl = null)
    {
        if (string.IsNullOrEmpty(jti)) return;

        var key = $"{TokenBlacklistPrefix}{jti}";
        var expiry = ttl ?? TimeSpan.FromHours(24);
        
        await _db.StringSetAsync(key, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), expiry);
        _logger.LogInformation("Token revoked: jti={Jti}, expires in {Expiry}", jti, expiry);
    }

    public async Task RevokeAllUserTokensAsync(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return;

        var key = $"{UserRevocationPrefix}{userId}";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await _db.StringSetAsync(key, timestamp.ToString(), TimeSpan.FromDays(7));
        _logger.LogInformation("All tokens revoked for user {UserId} at {Timestamp}", userId, timestamp);
    }

    public async Task<bool> IsTokenRevokedAsync(string jti)
    {
        if (string.IsNullOrEmpty(jti)) return false;

        var key = $"{TokenBlacklistPrefix}{jti}";
        return await _db.KeyExistsAsync(key);
    }
}

/// <summary>
///     Extension methods for token revocation
/// </summary>
public static class TokenRevocationExtensions
{
    /// <summary>
    ///     Adds token revocation middleware.
    ///     Must be called after UseAuthentication() and before UseAuthorization()
    /// </summary>
    public static IApplicationBuilder UseTokenRevocation(this IApplicationBuilder app)
    {
        return app.UseMiddleware<TokenRevocationMiddleware>();
    }
}
