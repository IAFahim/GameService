using GameService.ServiceDefaults.Data;
using Microsoft.AspNetCore.Routing;

namespace GameService.ApiService.Features.Auth;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth");
        group.MapIdentityApi<ApplicationUser>();
    }
}