using GameService.ApiService;
using GameService.ApiService.Features.Auth;
using GameService.ApiService.Features.Economy;
using GameService.ApiService.Features.Players;
using GameService.ApiService.Hubs;
using GameService.ApiService.Infrastructure.Data;
using GameService.ApiService.Infrastructure.Security;
using GameService.ServiceDefaults.Data;
using Microsoft.AspNetCore.Identity;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddNpgsqlDbContext<GameDbContext>("postgresdb");
builder.AddRedisClient("cache");

// Optimization: Use Source Generators for JSON
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, GameJsonContext.Default);
});

builder.Services.AddAuthorization();
builder.Services.AddIdentityApiEndpoints<ApplicationUser>()
    .AddEntityFrameworkStores<GameDbContext>();

// Security: Use Argon2
builder.Services.AddScoped<IPasswordHasher<ApplicationUser>, Argon2PasswordHasher>();

// Features
builder.Services.AddScoped<IPlayerService, PlayerService>();
builder.Services.AddScoped<IEconomyService, EconomyService>();

// Real-time: SignalR
builder.Services.AddSignalR()
    .AddStackExchangeRedis(builder.Configuration.GetConnectionString("cache") ?? throw new InvalidOperationException("Redis connection string is missing"));

var app = builder.Build();

// Development Seeding
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    await DbInitializer.InitializeAsync(app.Services);
}

app.UseHttpsRedirection();

// Map Features
app.MapAuthEndpoints();
app.MapPlayerEndpoints();
app.MapEconomyEndpoints();

// Map Hubs
app.MapHub<GameHub>("/hubs/game");

app.MapDefaultEndpoints();
app.Run();