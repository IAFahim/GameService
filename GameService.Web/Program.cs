using GameService.ServiceDefaults.Data;
using GameService.ServiceDefaults.Security;
using GameService.Web;
using GameService.Web.Services;
using GameService.Web.Components;
using GameService.Web.Workers;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddRedisOutputCache("cache");
builder.AddRedisClient("cache"); 

// Services
builder.Services.AddSingleton<PlayerUpdateNotifier>();
builder.Services.AddHostedService<RedisLogStreamer>();

// DB: Use AddDbContextFactory for Blazor Server thread safety
builder.AddNpgsqlDbContext<GameDbContext>("postgresdb", settings => 
{
    // Optional: Settings customization
}, configureDbContextOptions: options => 
{
    // Best practice for Blazor: Enable sensitive data only in dev
    if (builder.Environment.IsDevelopment()) 
        options.EnableSensitiveDataLogging();
});

// Register the Factory explicitly used by components
builder.Services.AddDbContextFactory<GameDbContext>(options => { });

// Identity
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

builder.Services.AddIdentityCore<ApplicationUser>(options => 
    {
        options.SignIn.RequireConfirmedAccount = true;
    })
    .AddEntityFrameworkStores<GameDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

// Security: Use Argon2 (Shared with API)
builder.Services.AddScoped<IPasswordHasher<ApplicationUser>, Argon2PasswordHasher>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Migrations
if (app.Environment.IsDevelopment())
{
    // Create a scope just for migration check
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
        await db.Database.EnsureCreatedAsync();
    }
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();
app.UseOutputCache();
app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapAdditionalIdentityEndpoints();
app.MapDefaultEndpoints();

app.Run();