using System.Security.Claims;
using GameService.ServiceDefaults.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace GameService.ApiService.Features.Economy;

public static class EconomyEndpoints
{
    public static void MapEconomyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/game").RequireAuthorization();

        group.MapPost("/coins/transaction", ProcessTransaction);
    }

    private static async Task<IResult> ProcessTransaction(
        [FromBody] UpdateCoinRequest req, 
        HttpContext ctx, 
        IEconomyService service)
    {
        var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var result = await service.ProcessTransactionAsync(userId, req.Amount);

        if (!result.Success)
        {
            // Determine if it's a 400 or 409 based on error message or add ErrorType enum
            // For simplicity, if error contains "concurrent", return 409
            if (result.Error?.Contains("concurrent") == true)
                return Results.Conflict(result.Error);
            
            return Results.BadRequest(result.Error);
        }

        return Results.Ok(new { NewBalance = result.NewBalance });
    }
}