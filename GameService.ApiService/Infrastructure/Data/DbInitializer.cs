using GameService.ServiceDefaults.Configuration;
using GameService.ServiceDefaults.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GameService.ApiService.Infrastructure.Data;

public static class DbInitializer
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<GameServiceOptions>>().Value;
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<GameDbContext>>();
        var env = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();

        if (env.IsDevelopment() || env.IsEnvironment("Testing"))
        {
            await db.Database.EnsureCreatedAsync();
            logger.LogInformation("Database schema ensured (Development mode - using EnsureCreated)");
        }
        else
        {
            var pendingMigrations = await db.Database.GetPendingMigrationsAsync();
            if (pendingMigrations.Any())
            {
                logger.LogInformation("Applying {Count} pending migration(s): {Migrations}",
                    pendingMigrations.Count(),
                    string.Join(", ", pendingMigrations));

                await db.Database.MigrateAsync();

                logger.LogInformation("Database migrations applied successfully");
            }
            else
            {
                logger.LogInformation("Database is up to date - no pending migrations");
            }
        }

        if (!await roleManager.RoleExistsAsync("Admin"))
            await roleManager.CreateAsync(new IdentityRole("Admin"));

        var adminEmail = options.AdminSeed.Email;
        var adminPassword = options.AdminSeed.Password;

        if (string.IsNullOrEmpty(adminEmail) || string.IsNullOrEmpty(adminPassword))
        {
            if (env.IsDevelopment())
                logger.LogWarning(
                    "Admin seed skipped. Please check 'GameService:AdminSeed' in appsettings.Development.json");
            else
                logger.LogInformation(
                    "Admin seed credentials not configured in production. " +
                    "Set environment variables: GameService__AdminSeed__Email and GameService__AdminSeed__Password");
            return;
        }

        if (await userManager.FindByEmailAsync(adminEmail) is null)
        {
            var admin = new ApplicationUser
            {
                UserName = adminEmail,
                Email = adminEmail,
                EmailConfirmed = true
            };

            var result = await userManager.CreateAsync(admin, adminPassword);
            if (result.Succeeded)
            {
                await userManager.AddToRoleAsync(admin, "Admin");
                db.PlayerProfiles.Add(new PlayerProfile
                {
                    UserId = admin.Id,
                    Coins = options.AdminSeed.InitialCoins
                });
                await db.SaveChangesAsync();

                if (env.IsDevelopment())
                {
                    logger.LogInformation("✅ Admin initialized from config.");
                    logger.LogInformation("   Email: {Email}", adminEmail);
                    logger.LogInformation("   Password: {Password}", adminPassword);
                }
                else
                {
                    logger.LogInformation("Admin account created successfully");
                }
            }
            else
            {
                logger.LogError(
                    "Failed to create admin account: {Errors}",
                    string.Join(", ", result.Errors.Select(e => e.Description)));
            }
        }
    }
}