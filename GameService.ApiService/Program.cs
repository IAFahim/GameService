using System.Threading.RateLimiting;
using GameService.ApiService;
using GameService.ApiService.Features.Admin;
using GameService.ApiService.Features.Auth;
using GameService.ApiService.Features.Common;
using GameService.ApiService.Features.Economy;
using GameService.ApiService.Features.Games;
using GameService.ApiService.Features.Players;
using GameService.ApiService.Hubs;
using GameService.ApiService.Infrastructure;
using GameService.ApiService.Infrastructure.Data;
using GameService.ApiService.Infrastructure.Redis;
using GameService.ApiService.Infrastructure.Workers;
using GameService.GameCore;
using GameService.LuckyMine;
using GameService.Ludo;
using GameService.ServiceDefaults;
using GameService.ServiceDefaults.Configuration;
using GameService.ServiceDefaults.Data;
using GameService.ServiceDefaults.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

var gameServiceOptions = builder.Configuration.GetSection(GameServiceOptions.SectionName).Get<GameServiceOptions>() ??
                         new GameServiceOptions();
var dbOptions = gameServiceOptions.Database;

builder.AddNpgsqlDbContext<GameDbContext>("postgresdb", configureDbContextOptions: options =>
{
    options.UseNpgsql(npgsqlOptions =>
    {
        npgsqlOptions.EnableRetryOnFailure(
            3,
            TimeSpan.FromSeconds(5),
            null);
        npgsqlOptions.CommandTimeout(dbOptions.CommandTimeout);
    });

    if (builder.Environment.IsDevelopment())
    {
        options.EnableSensitiveDataLogging();
        options.EnableDetailedErrors();
    }
});

builder.AddRedisClient("cache");

builder.Services.Configure<GameServiceOptions>(builder.Configuration.GetSection(GameServiceOptions.SectionName));
builder.Services.Configure<AdminSettings>(builder.Configuration.GetSection(AdminSettings.SectionName));

builder.Services.AddSecurityValidation();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, GameJsonContext.Default);
    options.SerializerOptions.TypeInfoResolverChain.Insert(1, LudoJsonContext.Default);
    options.SerializerOptions.TypeInfoResolverChain.Insert(2, LuckyMineJsonContext.Default);
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
});

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (builder.Environment.IsDevelopment())
        {
            policy.SetIsOriginAllowed(origin => new Uri(origin).Host == "localhost")
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials();
        }
        else
        {
            policy.WithOrigins(gameServiceOptions.Cors.AllowedOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials();
        }
    });
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        if (httpContext.Request.Path.StartsWithSegments("/admin")) return RateLimitPartition.GetNoLimiter("admin");

        if (httpContext.Request.Path.StartsWithSegments("/health") ||
            httpContext.Request.Path.StartsWithSegments("/alive"))
            return RateLimitPartition.GetNoLimiter("health");

        return RateLimitPartition.GetFixedWindowLimiter(
            httpContext.User.Identity?.Name ?? httpContext.Request.Headers.Host.ToString(),
            _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = gameServiceOptions.RateLimit.PermitLimit,
                QueueLimit = 0,
                Window = TimeSpan.FromMinutes(gameServiceOptions.RateLimit.WindowMinutes)
            });
    });
});

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminPolicy", policy => policy.RequireRole("Admin"));
});

builder.Services.AddIdentityApiEndpoints<ApplicationUser>()
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<GameDbContext>()
    .AddUserValidator<SystemUserValidator>();

builder.Services.Configure<IdentityOptions>(options =>
{
    options.SignIn.RequireConfirmedAccount = false;
    options.User.RequireUniqueEmail = true;
    options.Password.RequireNonAlphanumeric = true;
    options.Password.RequiredLength = 6;
});

builder.Services.AddScoped<IPasswordHasher<ApplicationUser>, Argon2PasswordHasher>();

builder.Services.AddSingleton<IRoomRegistry, RedisRoomRegistry>();
builder.Services.AddSingleton<IStateMigrationRegistry, StateMigrationRegistry>();
builder.Services.AddSingleton<IGameRepositoryFactory, RedisGameRepositoryFactory>();
builder.Services.AddSingleton<IGameEventPublisher, RedisGameEventPublisher>();
builder.Services.AddSingleton<IGameBroadcaster, HubGameBroadcaster>();
builder.Services.AddSingleton<ITokenRevocationService, RedisTokenRevocationService>();

builder.Services.AddScoped<IPlayerService, PlayerService>();
builder.Services.AddScoped<IEconomyService, EconomyService>();
builder.Services.AddScoped<IGameArchivalService, GameArchivalService>();

builder.Services.AddGameModule<LudoModule>();
builder.Services.AddGameModule<LuckyMineModule>();

builder.Services.AddHostedService<GameLoopWorker>();
builder.Services.AddHostedService<IdempotencyCleanupWorker>();
builder.Services.AddHostedService<OutboxProcessorWorker>();
builder.Services.AddHostedService<GameStateSnapshotWorker>();

builder.Services.AddSignalR()
    .AddStackExchangeRedis(builder.Configuration.GetConnectionString("cache") ??
                           throw new InvalidOperationException("Redis connection string is missing"))
    .AddJsonProtocol();

var app = builder.Build();

app.ValidateSecurity();

if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
{
    app.MapOpenApi();
    await DbInitializer.InitializeAsync(app.Services);
}
else
{
    if (gameServiceOptions.Security.RequireHttpsInProduction)
    {
        app.UseHttpsRedirection();
        app.UseHsts();
    }
}

app.UseCors();
app.UseRateLimiter();

app.UseAuthentication();

app.UseApiKeyAuthentication();
app.UseTokenRevocation();

app.UseAuthorization();

app.MapAuthEndpoints();
app.MapPlayerEndpoints();
app.MapEconomyEndpoints();
app.MapAdminEndpoints();
app.MapGameCatalogEndpoints();

app.MapHub<GameHub>("/hubs/game");

foreach (var module in app.Services.GetServices<IGameModule>()) module.MapEndpoints(app);

app.MapDefaultEndpoints();
app.Run();