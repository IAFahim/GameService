using System.ComponentModel.DataAnnotations;
using GameService.ServiceDefaults.Configuration;
using GameService.ServiceDefaults.DTOs;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GameService.ServiceDefaults.Data;

public class GameDbContext : IdentityDbContext<ApplicationUser>
{
    private readonly long _initialCoins;
    private readonly IGameEventPublisher? _publisher;

    public GameDbContext(DbContextOptions<GameDbContext> options, IGameEventPublisher? publisher = null,
        IOptions<GameServiceOptions>? gameOptions = null)
        : base(options)
    {
        _publisher = publisher;
        _initialCoins = gameOptions?.Value.Economy.InitialCoins ?? 100;
    }

    public DbSet<PlayerProfile> PlayerProfiles => Set<PlayerProfile>();
    public DbSet<GlobalSetting> GlobalSettings => Set<GlobalSetting>();
    public DbSet<GameRoomTemplate> RoomTemplates => Set<GameRoomTemplate>();
    public DbSet<WalletTransaction> WalletTransactions => Set<WalletTransaction>();
    public DbSet<ArchivedGame> ArchivedGames => Set<ArchivedGame>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<GameStateSnapshot> GameStateSnapshots => Set<GameStateSnapshot>();
    public DbSet<PlayerGameProgression> PlayerGameProgressions => Set<PlayerGameProgression>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<PlayerGameProgression>(b =>
        {
            b.HasIndex(p => p.UserId);
            b.HasIndex(p => p.GameType);
            b.HasIndex(p => new { p.UserId, p.GameType }).IsUnique();
        });

        builder.Entity<ApplicationUser>(b =>
        {
            b.HasOne(u => u.Profile)
                .WithOne(p => p.User)
                .HasForeignKey<PlayerProfile>(p => p.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PlayerProfile>()
            .HasIndex(p => p.UserId)
            .IsUnique();

        builder.Entity<WalletTransaction>(b =>
        {
            b.HasIndex(t => t.UserId);
            b.HasIndex(t => t.CreatedAt);
            b.HasIndex(t => t.IdempotencyKey).IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");
            b.HasIndex(t => t.ReferenceId);

            b.HasIndex(t => new { t.UserId, t.CreatedAt })
                .HasDatabaseName("IX_WalletTransactions_UserId_CreatedAt");

            b.HasIndex(t => new { t.UserId, t.ReferenceId })
                .HasDatabaseName("IX_WalletTransactions_UserId_ReferenceId");

            b.HasIndex(t => new { t.UserId, t.TransactionType, t.CreatedAt })
                .HasDatabaseName("IX_WalletTransactions_UserId_Type_CreatedAt");
        });

        builder.Entity<ArchivedGame>(b =>
        {
            b.HasIndex(g => g.RoomId);
            b.HasIndex(g => g.GameType);
            b.HasIndex(g => g.EndedAt);

            b.HasIndex(g => new { g.GameType, g.EndedAt })
                .HasDatabaseName("IX_ArchivedGames_GameType_EndedAt");

            b.HasIndex(g => new { g.WinnerUserId, g.EndedAt })
                .HasDatabaseName("IX_ArchivedGames_WinnerUserId_EndedAt");

            b.HasIndex(g => new { g.GameType, g.TotalPot })
                .HasDatabaseName("IX_ArchivedGames_GameType_TotalPot");
        });

        builder.Entity<OutboxMessage>(b =>
        {
            b.HasIndex(m => new { m.ProcessedAt, m.CreatedAt })
                .HasDatabaseName("IX_OutboxMessages_ProcessedAt_CreatedAt")
                .HasFilter("\"ProcessedAt\" IS NULL");

            b.HasIndex(m => m.ProcessedAt)
                .HasDatabaseName("IX_OutboxMessages_ProcessedAt");
        });

        builder.Entity<GameStateSnapshot>(b =>
        {
            b.HasIndex(s => s.RoomId).IsUnique();
            b.HasIndex(s => s.GameType);
            b.HasIndex(s => s.SnapshotAt);
        });

        builder.Entity<GameRoomTemplate>().HasData(
            new GameRoomTemplate
                { Id = 1, Name = "Classic Ludo (4P)", GameType = "Ludo", MaxPlayers = 4, EntryFee = 100 },
            new GameRoomTemplate { Id = 2, Name = "1v1 Ludo", GameType = "Ludo", MaxPlayers = 2, EntryFee = 500 },
            new GameRoomTemplate { Id = 5, Name = "StandardLudo", GameType = "Ludo", MaxPlayers = 4, EntryFee = 0 },
            new GameRoomTemplate
            {
                Id = 3, Name = "Standard Mines", GameType = "LuckyMine", MaxPlayers = 1, EntryFee = 10,
                ConfigJson = "{\"TotalMines\":5,\"TotalTiles\":25}"
            },
            new GameRoomTemplate
            {
                Id = 6, Name = "3Mines", GameType = "LuckyMine", MaxPlayers = 1, EntryFee = 0,
                ConfigJson = "{\"TotalMines\":3,\"TotalTiles\":25}"
            },
            new GameRoomTemplate
            {
                Id = 7, Name = "5Mines", GameType = "LuckyMine", MaxPlayers = 1, EntryFee = 0,
                ConfigJson = "{\"TotalMines\":5,\"TotalTiles\":25}"
            },
            new GameRoomTemplate
            {
                Id = 4, Name = "High Risk Mines", GameType = "LuckyMine", MaxPlayers = 1, EntryFee = 100,
                ConfigJson = "{\"TotalMines\":15,\"TotalTiles\":25}"
            }
        );
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var newUsers = ChangeTracker.Entries<ApplicationUser>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity)
            .ToList();

        long effectiveInitialCoins = _initialCoins;
        if (newUsers.Count > 0)
        {
            var dbSetting = await GlobalSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Key == "Economy:InitialCoins", cancellationToken);
            
            if (dbSetting != null && long.TryParse(dbSetting.Value, out var val))
            {
                effectiveInitialCoins = val;
            }
        }

        foreach (var user in newUsers)
        {
            var hasProfile = ChangeTracker.Entries<PlayerProfile>()
                .Any(e => e.State == EntityState.Added && e.Entity.User == user);

            if (!hasProfile && user.Profile == null)
            {
                PlayerProfiles.Add(new PlayerProfile
                {
                    User = user,
                    UserId = user.Id,
                    Coins = effectiveInitialCoins,
                    Version = Guid.NewGuid()
                });
            }
        }

        var addedProfiles = new List<PlayerProfile>();
        if (_publisher != null)
            addedProfiles = ChangeTracker.Entries<PlayerProfile>()
                .Where(e => e.State == EntityState.Added)
                .Select(e => e.Entity)
                .ToList();

        var result = await base.SaveChangesAsync(cancellationToken);

        if (result > 0 && addedProfiles.Count > 0 && _publisher != null)
            foreach (var profile in addedProfiles)
            {
                var username = profile.User?.UserName ?? "New Player";
                var email = profile.User?.Email ?? "Unknown";

                var message = new PlayerUpdatedMessage(
                    profile.UserId,
                    profile.Coins,
                    username,
                    email,
                    PlayerChangeType.Updated,
                    profile.Id);

                _ = Task.Run(() => _publisher.PublishPlayerUpdatedAsync(message), cancellationToken);
            }

        return result;
    }
}

public class ApplicationUser : IdentityUser
{
    public PlayerProfile? Profile { get; set; }
}

public class PlayerProfile
{
    public int Id { get; set; }

    [MaxLength(450)] public required string UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;

    public long Coins { get; set; } = 100;

    [ConcurrencyCheck] public Guid Version { get; set; } = Guid.NewGuid();

    public bool IsDeleted { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public DateTimeOffset? LastDailyLogin { get; set; }
    public int DailyLoginStreak { get; set; }
    public DateTimeOffset? LastDailySpin { get; set; }
    public int? AvatarId { get; set; }
    public string InventoryJson { get; set; } = "{}";
}

public class GlobalSetting
{
    [Key]
    [MaxLength(100)]
    public required string Key { get; set; }

    public required string Value { get; set; }

    [MaxLength(255)]
    public string? Description { get; set; }
}

public class GameRoomTemplate
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string GameType { get; set; }
    public int MaxPlayers { get; set; } = 4;
    public long EntryFee { get; set; }
    public string? ConfigJson { get; set; }
}

public class WalletTransaction
{
    public long Id { get; set; }

    [MaxLength(450)] public required string UserId { get; set; }

    public long Amount { get; set; }

    public long BalanceAfter { get; set; }

    [MaxLength(50)]
    public string TransactionType { get; set; } = "Unknown";

    [MaxLength(255)]
    public string Description { get; set; } = "";

    [MaxLength(100)]
    public string ReferenceId { get; set; } = "";

    [MaxLength(64)]
    public string? IdempotencyKey { get; set; }

    [MaxLength(20)]
    public string Currency { get; set; } = "Coin";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ArchivedGame
{
    public long Id { get; set; }

    [MaxLength(50)] public required string RoomId { get; set; }

    [MaxLength(50)] public required string GameType { get; set; }

    public string FinalStateJson { get; set; } = "{}";

    public string EventsJson { get; set; } = "[]";

    public string PlayerSeatsJson { get; set; } = "{}";

    [MaxLength(450)]
    public string? WinnerUserId { get; set; }

    public long TotalPot { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class OutboxMessage
{
    public long Id { get; set; }

    [MaxLength(100)]
    public required string EventType { get; set; }

    public required string Payload { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? ProcessedAt { get; set; }

    public int Attempts { get; set; }

    [MaxLength(500)]
    public string? LastError { get; set; }
}

public class GameStateSnapshot
{
    public long Id { get; set; }

    [MaxLength(50)]
    public required string RoomId { get; set; }

    [MaxLength(50)]
    public required string GameType { get; set; }

    public required byte[] StateData { get; set; }

    public required string MetaJson { get; set; }

    public DateTimeOffset SnapshotAt { get; set; } = DateTimeOffset.UtcNow;
}

public class PlayerGameProgression
{
    public int Id { get; set; }

    [MaxLength(450)]
    public required string UserId { get; set; }

    [MaxLength(50)]
    public required string GameType { get; set; }

    public int DailyLoginStreak { get; set; }

    public DateTimeOffset LastDailyLogin { get; set; }

    public bool HasClaimedWelcomeBonus { get; set; }
}