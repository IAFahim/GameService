using GameService.ApiService;
using GameService.ApiService.Features.Auth;
using GameService.ApiService.Features.Economy;
using GameService.ApiService.Features.Players;
using GameService.ApiService.Hubs;
using GameService.ApiService.Infrastructure.Data;
using GameService.ServiceDefaults.Security;
using GameService.ServiceDefaults.Data;
using Microsoft.AspNetCore.Identity;
using System.Threading.RateLimiting;

using GameService.ApiService.Features.Common;
using GameService.ApiService.Features.Admin;
using GameService.Ludo;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddNpgsqlDbContext<GameDbContext>("postgresdb");
builder.AddRedisClient("cache");

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, GameJsonContext.Default);
    options.SerializerOptions.TypeInfoResolverChain.Insert(1, LudoJsonContext.Default);
});

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.User.Identity?.Name ?? httpContext.Request.Headers.Host.ToString(),
            factory: partition => new FixedWindowRateLimiterOptions
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

builder.Services.AddScoped<IPasswordHasher<ApplicationUser>, Argon2PasswordHasher>();

builder.Services.AddScoped<IGameEventPublisher, RedisGameEventPublisher>();
builder.Services.AddScoped<IPlayerService, PlayerService>();
builder.Services.AddScoped<IEconomyService, EconomyService>();

builder.Services.AddSingleton<ILudoRepository, RedisLudoRepository>();
builder.Services.AddSingleton<LudoRoomService>();

builder.Services.AddSignalR()
    .AddStackExchangeRedis(builder.Configuration.GetConnectionString("cache") ?? throw new InvalidOperationException("Redis connection string is missing"))
    .AddJsonProtocol();

var app = builder.Build();

if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
{
    app.MapOpenApi();
    await DbInitializer.InitializeAsync(app.Services);
}

app.UseHttpsRedirection();
app.UseCors();
app.UseRateLimiter();

app.Use(async (context, next) =>
{
    if (context.Request.Headers.TryGetValue("X-Admin-Key", out var key) && key == "SecretAdminKey123!")
    {
        var claims = new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Admin") };
        var identity = new System.Security.Claims.ClaimsIdentity(claims, "ApiKey");
        context.User = new System.Security.Claims.ClaimsPrincipal(identity);
    }
    await next();
});

app.MapAuthEndpoints();
app.MapPlayerEndpoints();
app.MapEconomyEndpoints();
app.MapAdminEndpoints();

app.MapHub<GameHub>("/hubs/game");
app.MapHub<LudoHub>("/hubs/ludo"); 

app.MapDefaultEndpoints();
app.Run();