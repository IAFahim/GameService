using GameService.ServiceDefaults.Configuration;
using GameService.ServiceDefaults.Data;
using Microsoft.AspNetCore.Identity;
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

        await db.Database.EnsureCreatedAsync();

        if (!await roleManager.RoleExistsAsync("Admin")) 
            await roleManager.CreateAsync(new IdentityRole("Admin"));

        var adminEmail = options.AdminSeed.Email;
        var adminPassword = options.AdminSeed.Password;

        // In production, credentials MUST come from environment variables/secrets
        if (!env.IsDevelopment())
        {
            if (string.IsNullOrEmpty(adminEmail) || string.IsNullOrEmpty(adminPassword))
            {
                logger.LogInformation(
                    "Admin seed credentials not configured in production. " +
                    "Create admin account manually or set environment variables: " +
                    "GameService__AdminSeed__Email and GameService__AdminSeed__Password");
                return;
            }
        }
        else
        {
            // Development only - use configured values or skip
            if (string.IsNullOrEmpty(adminEmail) || string.IsNullOrEmpty(adminPassword))
            {
                logger.LogDebug("Admin seed skipped - no credentials configured in development");
                return;
            }
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
                
                // Don't log the email in production - security
                if (env.IsDevelopment())
                {
                    logger.LogInformation("Admin account created: {Email}", adminEmail);
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