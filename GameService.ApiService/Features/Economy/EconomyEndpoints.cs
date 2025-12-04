using System.Security.Claims;
using GameService.ServiceDefaults.Data;
using GameService.ServiceDefaults.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GameService.ApiService.Features.Economy;

public static class EconomyEndpoints
{
    public static void MapEconomyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/game").RequireAuthorization();

        group.MapPost("/coins/transaction", ProcessTransaction);
        group.MapGet("/coins/history", GetTransactionHistory);
    }

    private static async Task<IResult> ProcessTransaction(
        [FromBody] UpdateCoinRequest req,
        HttpContext ctx,
        IEconomyService service)
    {
        var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        // SECURITY: Users can only debit (spend) coins, not credit themselves
        // Credits must come from game engines or admin endpoints with valid ReferenceId
        if (req.Amount > 0)
            return Results.BadRequest("Users cannot credit coins directly. Use game actions to earn coins.");

        var result = await service.ProcessTransactionAsync(userId, req.Amount, req.ReferenceId, req.IdempotencyKey);

        if (!result.Success)
        {
            if (result.ErrorType == TransactionErrorType.ConcurrencyConflict)
                return Results.Conflict(result.ErrorMessage);

            if (result.ErrorType == TransactionErrorType.DuplicateTransaction)
                return Results.Conflict(result.ErrorMessage);

            return Results.BadRequest(result.ErrorMessage);
        }

        return Results.Ok(new { result.NewBalance });
    }

    private static async Task<IResult> GetTransactionHistory(
        HttpContext ctx,
        GameDbContext db,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        if (pageSize > 100) pageSize = 100;
        if (page < 1) page = 1;

        var transactions = await db.WalletTransactions
            .AsNoTracking()
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new WalletTransactionDto(
                t.Id,
                t.Amount,
                t.BalanceAfter,
                t.TransactionType,
                t.Description,
                t.ReferenceId,
                t.CreatedAt))
            .ToListAsync();

        return Results.Ok(transactions);
    }
}