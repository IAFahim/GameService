using GameService.Web;
using GameService.Web.Components;
using GameService.Web.Data;
using Microsoft.AspNetCore.Components.Authorization; // Add this
using Microsoft.AspNetCore.Identity; // Add this
using Microsoft.EntityFrameworkCore; // Add this

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();
builder.AddRedisOutputCache("cache");

// 1. Add Postgres Context for Identity (Uses Aspire Service Discovery)
builder.AddNpgsqlDbContext<ApplicationDbContext>("postgresdb");

// 2. Configure Identity
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

builder.Services.AddIdentityCore<ApplicationUser>(options => options.SignIn.RequireConfirmedAccount = true)
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddHttpClient<WeatherApiClient>(client =>
    {
        client.BaseAddress = new("https+http://apiservice");
    });

var app = builder.Build();

// 3. AUTOMATIC MIGRATION (Smart Solution for Dev)
// In production, use a separate migration bundle/job.
if (app.Environment.IsDevelopment())
{
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.EnsureCreatedAsync();
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();
app.UseOutputCache();
app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// 4. Add Identity Endpoints (Smart way to handle login/register API)
app.MapAdditionalIdentityEndpoints();

app.MapDefaultEndpoints();

app.Run();