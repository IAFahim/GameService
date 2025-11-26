using System.Security.Claims;
using GameService.ServiceDefaults.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace GameService.ApiService.Features.Players;

public static class PlayerEndpoints
{
    public static void MapPlayerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/game").RequireAuthorization();

        group.MapGet("/me", GetProfile);
    }

    private static async Task<IResult> GetProfile(HttpContext ctx, IPlayerService service)
    {
        var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var response = await service.GetProfileAsync(userId);
        return response.HasValue ? Results.Ok(response.Value) : Results.NotFound();
    }
}