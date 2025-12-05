using GameService.ServiceDefaults.Configuration;
using GameService.ServiceDefaults.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GameService.ApiService.Infrastructure.Data;

public static class DbInitializer
{
    /// <summary>
    /// Initialize database with migrations and seed data.
    /// In Development: Uses EnsureCreated for rapid iteration (no migration files needed).
    /// In Production: Uses Migrate() to apply pending migrations safely.
    /// 
    /// IMPORTANT: Before deploying to production, generate migrations:
    ///   dotnet ef migrations add InitialCreate -p GameService.ServiceDefaults -s GameService.ApiService
    /// </summary>
    public static async Task InitializeAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<GameServiceOptions>>().Value;
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<GameDbContext>>();
        var env = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();

        // Database initialization strategy:
        // - Development/Testing: Use EnsureCreated for quick iteration without migration files
        // - Production: Use Migrate() to apply pending migrations safely
        if (env.IsDevelopment() || env.IsEnvironment("Testing"))
        {
            // Fast path for development - creates schema from model without migrations
            // WARNING: This does not support schema updates. Delete DB or use migrations for changes.
            await db.Database.EnsureCreatedAsync();
            logger.LogInformation("Database schema ensured (Development mode - using EnsureCreated)");
        }
        else
        {
            // Production: Apply any pending migrations
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

        // Load credentials from appsettings.Development.json or Environment Variables
        var adminEmail = options.AdminSeed.Email;
        var adminPassword = options.AdminSeed.Password;

        if (string.IsNullOrEmpty(adminEmail) || string.IsNullOrEmpty(adminPassword))
        {
            if (env.IsDevelopment())
            {
                logger.LogWarning("Admin seed skipped. Please check 'GameService:AdminSeed' in appsettings.Development.json");
            }
            else
            {
                logger.LogInformation(
                    "Admin seed credentials not configured in production. " +
                    "Set environment variables: GameService__AdminSeed__Email and GameService__AdminSeed__Password");
            }
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
                
                // In Development, print the credentials to the console for convenience
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