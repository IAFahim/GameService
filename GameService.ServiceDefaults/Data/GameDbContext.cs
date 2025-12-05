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
    private readonly IGameEventPublisher? _publisher;
    private readonly long _initialCoins;

    public GameDbContext(DbContextOptions<GameDbContext> options, IGameEventPublisher? publisher = null, IOptions<GameServiceOptions>? gameOptions = null)
        : base(options)
    {
        _publisher = publisher;
        _initialCoins = gameOptions?.Value.Economy.InitialCoins ?? 100;
    }

    public DbSet<PlayerProfile> PlayerProfiles => Set<PlayerProfile>();
    public DbSet<GameRoomTemplate> RoomTemplates => Set<GameRoomTemplate>();
    public DbSet<WalletTransaction> WalletTransactions => Set<WalletTransaction>();
    public DbSet<ArchivedGame> ArchivedGames => Set<ArchivedGame>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

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
            // Single column indexes
            b.HasIndex(t => t.UserId);
            b.HasIndex(t => t.CreatedAt);
            b.HasIndex(t => t.IdempotencyKey).IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");
            b.HasIndex(t => t.ReferenceId);
            
            // Composite indexes for common query patterns
            // Query: Get user's recent transactions (sorted by date)
            b.HasIndex(t => new { t.UserId, t.CreatedAt })
                .HasDatabaseName("IX_WalletTransactions_UserId_CreatedAt");
            
            // Query: Find transactions by reference and user
            b.HasIndex(t => new { t.UserId, t.ReferenceId })
                .HasDatabaseName("IX_WalletTransactions_UserId_ReferenceId");
            
            // Query: Transaction type filtering for user
            b.HasIndex(t => new { t.UserId, t.TransactionType, t.CreatedAt })
                .HasDatabaseName("IX_WalletTransactions_UserId_Type_CreatedAt");
        });

        builder.Entity<ArchivedGame>(b =>
        {
            // Single column indexes
            b.HasIndex(g => g.RoomId);
            b.HasIndex(g => g.GameType);
            b.HasIndex(g => g.EndedAt);
            
            // Composite indexes for common query patterns
            // Query: Get games by type sorted by end time (leaderboards, history)
            b.HasIndex(g => new { g.GameType, g.EndedAt })
                .HasDatabaseName("IX_ArchivedGames_GameType_EndedAt");
            
            // Query: Find player's game history
            b.HasIndex(g => new { g.WinnerUserId, g.EndedAt })
                .HasDatabaseName("IX_ArchivedGames_WinnerUserId_EndedAt");
            
            // Query: Game type analytics with pot amounts
            b.HasIndex(g => new { g.GameType, g.TotalPot })
                .HasDatabaseName("IX_ArchivedGames_GameType_TotalPot");
        });

        builder.Entity<GameRoomTemplate>().HasData(
            new GameRoomTemplate
                { Id = 1, Name = "Classic Ludo (4P)", GameType = "Ludo", MaxPlayers = 4, EntryFee = 100 },
            new GameRoomTemplate { Id = 2, Name = "1v1 Ludo", GameType = "Ludo", MaxPlayers = 2, EntryFee = 500 },
            new GameRoomTemplate
            {
                Id = 3, Name = "Standard Mines", GameType = "LuckyMine", MaxPlayers = 1, EntryFee = 10,
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

        foreach (var user in newUsers)
        {
            var hasProfile = ChangeTracker.Entries<PlayerProfile>()
                .Any(e => e.State == EntityState.Added && e.Entity.User == user);

            if (!hasProfile && user.Profile == null)
                PlayerProfiles.Add(new PlayerProfile
                {
                    User = user,
                    UserId = user.Id,
                    Coins = _initialCoins,
                    Version = Guid.NewGuid()
                });
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

    /// <summary>Soft delete flag - preserves referential integrity with history tables</summary>
    public bool IsDeleted { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }
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

/// <summary>
///     Immutable transaction ledger for auditability - every coin movement is logged here.
/// </summary>
public class WalletTransaction
{
    public long Id { get; set; }

    [MaxLength(450)] public required string UserId { get; set; }

    /// <summary>Amount of coins (positive for credit, negative for debit)</summary>
    public long Amount { get; set; }

    /// <summary>Balance after this transaction</summary>
    public long BalanceAfter { get; set; }

    /// <summary>Transaction type: Deposit, Withdrawal, EntryFee, Win, Refund, AdminAdjust</summary>
    [MaxLength(50)]
    public string TransactionType { get; set; } = "Unknown";

    /// <summary>Human-readable description</summary>
    [MaxLength(255)]
    public string Description { get; set; } = "";

    /// <summary>Reference to related entity (RoomId, OrderId, etc.)</summary>
    [MaxLength(100)]
    public string ReferenceId { get; set; } = "";

    /// <summary>Idempotency key to prevent duplicate transactions</summary>
    [MaxLength(64)]
    public string? IdempotencyKey { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
///     Archived game record for history and replay capability.
/// </summary>
public class ArchivedGame
{
    public long Id { get; set; }

    [MaxLength(50)] public required string RoomId { get; set; }

    [MaxLength(50)] public required string GameType { get; set; }

    /// <summary>Serialized final game state</summary>
    public string FinalStateJson { get; set; } = "{}";

    /// <summary>Serialized list of game events for replay</summary>
    public string EventsJson { get; set; } = "[]";

    /// <summary>Player seats at end of game (UserId -> Seat)</summary>
    public string PlayerSeatsJson { get; set; } = "{}";

    /// <summary>Winner's UserId (null if draw/cancelled)</summary>
    [MaxLength(450)]
    public string? WinnerUserId { get; set; }

    public long TotalPot { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; } = DateTimeOffset.UtcNow;
}