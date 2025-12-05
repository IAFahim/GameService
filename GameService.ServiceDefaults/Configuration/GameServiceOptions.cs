namespace GameService.ServiceDefaults.Configuration;

/// <summary>
/// Central configuration options for the GameService platform.
/// </summary>
public class GameServiceOptions
{
    public const string SectionName = "GameService";

    /// <summary>
    /// Economy-related settings (coins, transactions, etc.)
    /// </summary>
    public EconomyOptions Economy { get; set; } = new();

    /// <summary>
    /// Game session and connection settings
    /// </summary>
    public SessionOptions Session { get; set; } = new();

    /// <summary>
    /// Admin account seeding settings
    /// </summary>
    public AdminSeedOptions AdminSeed { get; set; } = new();

    /// <summary>
    /// Rate limiting settings
    /// </summary>
    public RateLimitOptions RateLimit { get; set; } = new();

    /// <summary>
    /// CORS settings
    /// </summary>
    public CorsOptions Cors { get; set; } = new();

    /// <summary>
    /// Game loop worker settings
    /// </summary>
    public GameLoopOptions GameLoop { get; set; } = new();

    /// <summary>
    /// Security settings
    /// </summary>
    public SecurityOptions Security { get; set; } = new();
}

public class EconomyOptions
{
    /// <summary>
    /// Initial coins given to new players
    /// </summary>
    public long InitialCoins { get; set; } = 100;

    /// <summary>
    /// Days to keep idempotency keys before cleanup
    /// </summary>
    public int IdempotencyKeyRetentionDays { get; set; } = 7;
}

public class SessionOptions
{
    /// <summary>
    /// Grace period in seconds for player reconnection before being removed from game
    /// </summary>
    public int ReconnectionGracePeriodSeconds { get; set; } = 15;
}

public class AdminSeedOptions
{
    /// <summary>
    /// Admin account email for seeding (required in production via env var)
    /// </summary>
    public string Email { get; set; } = "";

    /// <summary>
    /// Admin account password for seeding (required in production via env var)
    /// </summary>
    public string Password { get; set; } = "";

    /// <summary>
    /// Initial coins for admin account
    /// </summary>
    public long InitialCoins { get; set; } = 1_000_000;
}

public class RateLimitOptions
{
    /// <summary>
    /// Maximum number of requests per window
    /// </summary>
    public int PermitLimit { get; set; } = 100;

    /// <summary>
    /// Rate limit window in minutes
    /// </summary>
    public int WindowMinutes { get; set; } = 1;

    /// <summary>
    /// Maximum SignalR messages per minute per user
    /// </summary>
    public int SignalRMessagesPerMinute { get; set; } = 60;
}

public class CorsOptions
{
    /// <summary>
    /// Allowed origins for CORS (comma-separated in production)
    /// </summary>
    public string[] AllowedOrigins { get; set; } = ["https://yourdomain.com"];
}

public class GameLoopOptions
{
    /// <summary>
    /// Interval in milliseconds between game loop ticks
    /// </summary>
    public int TickIntervalMs { get; set; } = 5000;
}

public class SecurityOptions
{
    /// <summary>
    /// Require HTTPS in production environment
    /// </summary>
    public bool RequireHttpsInProduction { get; set; } = true;

    /// <summary>
    /// Minimum length for API keys (enforced in production)
    /// </summary>
    public int MinimumApiKeyLength { get; set; } = 32;

    /// <summary>
    /// Block requests if API key validation fails in production
    /// </summary>
    public bool EnforceApiKeyValidation { get; set; } = true;
}

/// <summary>
/// Admin API settings - separate from GameService options for cleaner separation
/// </summary>
public class AdminSettings
{
    public const string SectionName = "AdminSettings";
    
    /// <summary>
    /// API Key for admin access. MUST be set via environment variable in production.
    /// Use: AdminSettings__ApiKey or AdminSettings:ApiKey
    /// </summary>
    public string ApiKey { get; set; } = "";
}
