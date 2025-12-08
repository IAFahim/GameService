using System.Text.Json;
using GameService.ApiService.Features.Economy;
using GameService.ServiceDefaults.Data;

namespace GameService.ApiService.Features.Games;

public interface IGameArchivalService
{
    Task<GameEndResult> EndGameAsync(
        string roomId,
        string gameType,
        object finalState,
        IReadOnlyDictionary<string, int> playerSeats,
        string? winnerUserId,
        long totalPot,
        DateTimeOffset startedAt,
        IReadOnlyList<string>? winnerRanking = null);


    Task ArchiveGameAsync(
        string roomId,
        string gameType,
        object finalState,
        IReadOnlyDictionary<string, int> playerSeats,
        string? winnerUserId,
        long totalPot,
        DateTimeOffset startedAt);
}

public record GameEndResult(
    bool Success,
    IReadOnlyDictionary<string, long> Payouts,
    string? ErrorMessage = null);

public class GameArchivalService(
    GameDbContext db,
    IEconomyService economyService,
    ILogger<GameArchivalService> logger) : IGameArchivalService
{
    public async Task<GameEndResult> EndGameAsync(
        string roomId,
        string gameType,
        object finalState,
        IReadOnlyDictionary<string, int> playerSeats,
        string? winnerUserId,
        long totalPot,
        DateTimeOffset startedAt,
        IReadOnlyList<string>? winnerRanking = null)
    {
        try
        {
            var payoutResult = await economyService.ProcessGamePayoutsAsync(
                roomId,
                gameType,
                totalPot,
                playerSeats,
                winnerUserId,
                winnerRanking);

            if (!payoutResult.Success)
                logger.LogError("Failed to process payouts for game {RoomId}: {Error}",
                    roomId, payoutResult.ErrorMessage);

            await ArchiveGameAsync(roomId, gameType, finalState, playerSeats, winnerUserId, totalPot, startedAt);

            logger.LogInformation(
                "Game ended and archived: Room={RoomId}, Type={GameType}, Winner={Winner}, Pot={Pot}, Payouts={PayoutCount}",
                roomId, gameType, winnerUserId ?? "None", totalPot, payoutResult.Payouts.Count);

            return new GameEndResult(true, payoutResult.Payouts);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to end game {RoomId}", roomId);
            return new GameEndResult(false, new Dictionary<string, long>(), ex.Message);
        }
    }

    public async Task ArchiveGameAsync(
        string roomId,
        string gameType,
        object finalState,
        IReadOnlyDictionary<string, int> playerSeats,
        string? winnerUserId,
        long totalPot,
        DateTimeOffset startedAt)
    {
        try
        {
            var archivedGame = new ArchivedGame
            {
                RoomId = roomId,
                GameType = gameType,
                FinalStateJson = JsonSerializer.Serialize(finalState),
                PlayerSeatsJson = JsonSerializer.Serialize(playerSeats),
                WinnerUserId = winnerUserId,
                TotalPot = totalPot,
                StartedAt = startedAt,
                EndedAt = DateTimeOffset.UtcNow
            };

            db.ArchivedGames.Add(archivedGame);
            await db.SaveChangesAsync();

            logger.LogInformation("Archived game {RoomId} (Type: {GameType}, Winner: {Winner}, Pot: {Pot})",
                roomId, gameType, winnerUserId ?? "None", totalPot);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to archive game {RoomId}", roomId);
        }
    }
}