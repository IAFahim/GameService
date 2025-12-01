using GameService.ApiService;
using GameService.ApiService.Features.Auth;
using GameService.ApiService.Features.Economy;
using GameService.ApiService.Features.Players;
using GameService.ApiService.Hubs;
using GameService.ApiService.Infrastructure.Data;
using GameService.ApiService.Infrastructure.Redis;
using GameService.ServiceDefaults.Security;
using GameService.ServiceDefaults.Data;
using Microsoft.AspNetCore.Identity;
using System.Threading.RateLimiting;
using GameService.ServiceDefaults;
using GameService.ApiService.Features.Common;
using GameService.ApiService.Features.Admin;
using GameService.ApiService.Infrastructure;
using GameService.GameCore;
using GameService.LuckyMine;
using GameService.Ludo;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddNpgsqlDbContext<GameDbContext>("postgresdb");
builder.AddRedisClient("cache");

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
            policy.AllowAnyOrigin()
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        }
        else
        {
            policy.WithOrigins("https://yourdomain.com")
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials();
        }
    });
});

builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.User.Identity?.Name ?? httpContext.Request.Headers.Host.ToString(),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 100,
                QueueLimit = 0,
                Window = TimeSpan.FromMinutes(1)
            }));
});

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminPolicy", policy => policy.RequireRole("Admin"));
});

builder.Services.AddIdentityApiEndpoints<ApplicationUser>()
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<GameDbContext>();

builder.Services.Configure<IdentityOptions>(options => 
{
    options.SignIn.RequireConfirmedAccount = false; 
    options.User.RequireUniqueEmail = true;
    options.Password.RequireNonAlphanumeric = true;
    options.Password.RequiredLength = 6;
});

builder.Services.AddScoped<IPasswordHasher<ApplicationUser>, Argon2PasswordHasher>();

builder.Services.AddSingleton<IRoomRegistry, RedisRoomRegistry>();
builder.Services.AddSingleton<IGameRepositoryFactory, RedisGameRepositoryFactory>();
builder.Services.AddSingleton<IGameEventPublisher, RedisGameEventPublisher>();
builder.Services.AddSingleton<IGameBroadcaster, HubGameBroadcaster>();

builder.Services.AddScoped<IPlayerService, PlayerService>();
builder.Services.AddScoped<IEconomyService, EconomyService>();

builder.Services.AddGameModule<LudoModule>();
builder.Services.AddGameModule<LuckyMineModule>();

builder.Services.AddSignalR()
    .AddStackExchangeRedis(builder.Configuration.GetConnectionString("cache") ?? throw new InvalidOperationException("Redis connection string is missing"))
    .AddJsonProtocol();

var app = builder.Build();

if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
{
    app.MapOpenApi();
    await DbInitializer.InitializeAsync(app.Services);
}

app.UseCors();
app.UseRateLimiter();

app.UseAuthentication();

app.Use(async (context, next) =>
{
    var apiKey = context.Request.Headers["X-Admin-Key"].FirstOrDefault();
    var configuredKey = context.RequestServices.GetRequiredService<IConfiguration>()["AdminSettings:ApiKey"];

    if (!string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(configuredKey) && apiKey == configuredKey)
    {
        var claims = new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Admin") };
        var identity = new System.Security.Claims.ClaimsIdentity(claims, "ApiKey");
        context.User = new System.Security.Claims.ClaimsPrincipal(identity);
    }
    await next();
});

app.UseAuthorization();

app.MapAuthEndpoints();
app.MapPlayerEndpoints();
app.MapEconomyEndpoints();
app.MapAdminEndpoints();
GameService.ApiService.Features.Games.GameCatalogEndpoints.MapGameCatalogEndpoints(app);

app.MapHub<GameHub>("/hubs/game");

foreach (var module in app.Services.GetServices<IGameModule>())
{
    module.MapEndpoints(app);
}

app.MapDefaultEndpoints();
app.Run();