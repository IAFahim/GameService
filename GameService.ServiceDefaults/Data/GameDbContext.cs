using System.ComponentModel.DataAnnotations;
using GameService.ServiceDefaults.DTOs;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace GameService.ServiceDefaults.Data;

public class GameDbContext(DbContextOptions<GameDbContext> options, IGameEventPublisher? publisher = null) 
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<PlayerProfile> PlayerProfiles => Set<PlayerProfile>();
    public DbSet<GameRoomTemplate> RoomTemplates => Set<GameRoomTemplate>();

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

        builder.Entity<GameRoomTemplate>().HasData(
            new GameRoomTemplate { Id = 1, Name = "Classic Ludo (4P)", GameType = "Ludo", MaxPlayers = 4, EntryFee = 100 },
            new GameRoomTemplate { Id = 2, Name = "1v1 Ludo", GameType = "Ludo", MaxPlayers = 2, EntryFee = 500 },
            new GameRoomTemplate { Id = 3, Name = "Standard Mines", GameType = "LuckyMine", MaxPlayers = 20, EntryFee = 10, ConfigJson = "{\"TotalMines\":20,\"TotalTiles\":100}" },
            new GameRoomTemplate { Id = 4, Name = "Impossible Mines", GameType = "LuckyMine", MaxPlayers = 10, EntryFee = 100, ConfigJson = "{\"TotalMines\":90,\"TotalTiles\":100}" }
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
            {
                PlayerProfiles.Add(new PlayerProfile
                {
                    User = user,
                    UserId = user.Id,
                    Coins = 100,
                    Version = Guid.NewGuid()
                });
            }
        }

        var addedProfiles = new List<PlayerProfile>();
        if (publisher != null)
        {
            addedProfiles = ChangeTracker.Entries<PlayerProfile>()
                .Where(e => e.State == EntityState.Added)
                .Select(e => e.Entity)
                .ToList();
        }

        var result = await base.SaveChangesAsync(cancellationToken);

        if (result > 0 && addedProfiles.Count > 0 && publisher != null)
        {
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

                _ = Task.Run(() => publisher.PublishPlayerUpdatedAsync(message), cancellationToken);
            }
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

    [ConcurrencyCheck]
    public Guid Version { get; set; } = Guid.NewGuid();
}

public class GameRoomTemplate
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string GameType { get; set; }
    public int MaxPlayers { get; set; } = 4;
    public long EntryFee { get; set; } = 0;
    public string? ConfigJson { get; set; }
}