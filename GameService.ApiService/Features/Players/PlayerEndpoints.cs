using System.Security.Claims;
using GameService.ServiceDefaults.Data;
using GameService.ServiceDefaults.DTOs;
using GameService.ServiceDefaults.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GameService.ApiService.Features.Players;

public static class PlayerEndpoints
{
    public static void MapPlayerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/game").RequireAuthorization();

        group.MapGet("/me", GetProfile);
        group.MapPatch("/me", UpdateProfile);
        group.MapGet("/leaderboard", GetGlobalLeaderboard);
    }

    private static async Task<IResult> GetProfile(HttpContext ctx, IPlayerService service)
    {
        var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var response = await service.GetProfileAsync(userId);
        return response.HasValue ? Results.Ok(response.Value) : Results.NotFound();
    }

    private static async Task<IResult> UpdateProfile(
        [FromBody] UpdateProfileRequest req,
        HttpContext ctx,
        GameDbContext db)
    {
        var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        if (!string.IsNullOrEmpty(req.DisplayName) &&
            !InputValidator.IsValidTemplateName(req.DisplayName))
            return Results.BadRequest("Invalid display name format.");

        var user = await db.Users.FindAsync(userId);
        if (user == null) return Results.NotFound();

        if (!string.IsNullOrWhiteSpace(req.DisplayName))
            user.UserName = req.DisplayName;

        await db.SaveChangesAsync();

        return Results.Ok(new { Message = "Profile updated" });
    }

    private static async Task<IResult> GetGlobalLeaderboard(
        GameDbContext db,
        [FromQuery] int limit = 10)
    {
        limit = Math.Clamp(limit, 1, 100);

        var topPlayers = await db.PlayerProfiles
            .AsNoTracking()
            .Where(p => !p.IsDeleted)
            .OrderByDescending(p => p.Coins)
            .Take(limit)
            .Select(p => new LeaderboardEntryDto(
                p.User.UserName ?? "Unknown",
                p.Coins))
            .ToListAsync();

        return Results.Ok(topPlayers);
    }
}