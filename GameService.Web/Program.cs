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

builder.Services.AddSingleton<PlayerUpdateNotifier>();
builder.Services.AddHttpClient<GameAdminService>(client => 
{
    client.BaseAddress = new("http://apiservice");
});
builder.Services.AddHostedService<RedisLogStreamer>();

builder.AddNpgsqlDbContext<GameDbContext>("postgresdb", settings => 
{
}, configureDbContextOptions: options => 
{
    if (builder.Environment.IsDevelopment()) 
        options.EnableSensitiveDataLogging();
});

builder.Services.AddDbContextFactory<GameDbContext>(options => { });

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

builder.Services.AddScoped<IPasswordHasher<ApplicationUser>, Argon2PasswordHasher>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
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