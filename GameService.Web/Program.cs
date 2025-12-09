using GameService.ServiceDefaults.Configuration;
using GameService.ServiceDefaults.Data;
using GameService.ServiceDefaults.Security;
using GameService.Web;
using GameService.Web.Components;
using GameService.Web.Services;
using GameService.Web.Workers;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddRedisOutputCache("cache");
builder.AddRedisClient("cache");

builder.Services.Configure<GameServiceOptions>(builder.Configuration.GetSection(GameServiceOptions.SectionName));

builder.Services.AddSingleton<PlayerUpdateNotifier>();
builder.Services.AddScoped<ToastService>();
builder.Services.AddHttpClient<GameAdminService>((sp, client) =>
{
    client.BaseAddress = new Uri("http://apiservice");
    var config = sp.GetRequiredService<IConfiguration>();
    var apiKey = config["AdminSettings:ApiKey"] ?? "SecretAdminKey123!";
    client.DefaultRequestHeaders.Add("X-Admin-Key", apiKey);
});
builder.Services.AddHostedService<RedisLogStreamer>();

var webDbOptions = builder.Configuration
    .GetSection(GameServiceOptions.SectionName)
    .Get<GameServiceOptions>()?.Database ?? new DatabaseOptions();

builder.AddNpgsqlDbContext<GameDbContext>("postgresdb", configureDbContextOptions: options =>
{
    options.UseNpgsql(npgsqlOptions =>
    {
        npgsqlOptions.EnableRetryOnFailure(
            3,
            TimeSpan.FromSeconds(5),
            null);
        npgsqlOptions.CommandTimeout(webDbOptions.CommandTimeout);
    });

    if (builder.Environment.IsDevelopment())
    {
        options.EnableSensitiveDataLogging();
        options.EnableDetailedErrors();
    }
});

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityUserAccessor>();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

builder.Services.AddAuthorization();

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequiredLength = 6;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<GameDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddScoped<IPasswordHasher<ApplicationUser>, Argon2PasswordHasher>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", true);
    app.UseHsts();

    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    var pendingMigrations = await db.Database.GetPendingMigrationsAsync();
    if (pendingMigrations.Any())
    {
        logger.LogInformation("Applying {Count} pending migration(s)", pendingMigrations.Count());
        await db.Database.MigrateAsync();
    }
}
else
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.UseStaticFiles();
app.UseAntiforgery();
app.UseAuthentication();
app.UseAuthorization();
app.UseOutputCache();

app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapAdditionalIdentityEndpoints();
app.MapDefaultEndpoints();

app.Run();