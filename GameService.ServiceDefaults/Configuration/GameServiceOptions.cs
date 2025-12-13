namespace GameService.ServiceDefaults.Configuration;

public class GameServiceOptions
{
    public const string SectionName = "GameService";

    public EconomyOptions Economy { get; set; } = new();

    public SessionOptions Session { get; set; } = new();

    public AdminSeedOptions AdminSeed { get; set; } = new();

    public RateLimitOptions RateLimit { get; set; } = new();

    public CorsOptions Cors { get; set; } = new();

    public GameLoopOptions GameLoop { get; set; } = new();

    public SecurityOptions Security { get; set; } = new();

    public DatabaseOptions Database { get; set; } = new();
}

public class EconomyOptions
{
    public long InitialCoins { get; set; } = 100;

    public int IdempotencyKeyRetentionDays { get; set; } = 7;

    public string PaymentWebhookSecret { get; set; } = "";
}

public class SessionOptions
{
    public int ReconnectionGracePeriodSeconds { get; set; } = 15;

    public int MaxConnectionsPerUser { get; set; } = 3;
}

public class AdminSeedOptions
{
    public string Email { get; set; } = "";

    public string Password { get; set; } = "";

    public long InitialCoins { get; set; } = 1_000_000;
}

public class RateLimitOptions
{
    public int PermitLimit { get; set; } = 1000;

    public int WindowMinutes { get; set; } = 1;

    public int SignalRMessagesPerMinute { get; set; } = 120;
}

public class CorsOptions
{
    public string[] AllowedOrigins { get; set; } =
    [
        "http://172.252.13.99",
        "http://172.252.13.99:8080",
        "http://localhost"
    ];
}

public class GameLoopOptions
{
    public int TickIntervalMs { get; set; } = 5000;
}

public class SecurityOptions
{
    public bool RequireHttpsInProduction { get; set; } = true;

    public int MinimumApiKeyLength { get; set; } = 32;

    public bool EnforceApiKeyValidation { get; set; } = true;
}

public class DatabaseOptions
{
    public int MaxPoolSize { get; set; } = 500;

    public int MinPoolSize { get; set; } = 25;

    public int ConnectionIdleLifetime { get; set; } = 300;

    public int ConnectionTimeout { get; set; } = 30;

    public int CommandTimeout { get; set; } = 30;

    public bool Pooling { get; set; } = true;

    public string? ReadReplicaConnectionString { get; set; }
}

public class AdminSettings
{
    public const string SectionName = "AdminSettings";

    public string ApiKey { get; set; } = "";
}