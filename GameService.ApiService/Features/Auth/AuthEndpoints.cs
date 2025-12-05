using GameService.ServiceDefaults.Data;

namespace GameService.ApiService.Features.Auth;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth");
        group.MapIdentityApi<ApplicationUser>();

        // QoL: Logout endpoint for standard auth lifecycle compliance
        // Even though JWTs are stateless, having this endpoint prepares for token revocation later
        group.MapPost("/logout", () => Results.Ok(new { Message = "Logged out successfully" }))
            .RequireAuthorization()
            .WithName("Logout");
    }
}