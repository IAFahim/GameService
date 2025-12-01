using System.Text.Json;
using GameService.GameCore;
using Microsoft.Extensions.Logging;

namespace GameService.LuckyMine;

public sealed class LuckyMineEngine(
    IGameRepositoryFactory repoFactory,
    IRoomRegistry roomRegistry,
    ILogger<LuckyMineEngine> logger) : IGameEngine
{
    private readonly IGameRepository<LuckyMineState> _repository 
        = repoFactory.Create<LuckyMineState>("LuckyMine");

    public string GameType => "LuckyMine";

    public async Task<GameActionResult> ExecuteAsync(string roomId, GameCommand command)
    {
        return command.Action.ToLowerInvariant() switch
        {
            "click" => await HandleClickAsync(roomId, command.UserId, command.GetInt("tileIndex")),
            _ => GameActionResult.Error($"Unknown action: {command.Action}")
        };
    }

    private async Task<GameActionResult> HandleClickAsync(string roomId, string userId, int tileIndex)
    {
        var ctx = await _repository.LoadAsync(roomId);
        if (ctx == null) return GameActionResult.Error("Room not found");

        var state = ctx.State;

        if (!ctx.Meta.PlayerSeats.TryGetValue(userId, out var seat))
            return GameActionResult.Error("Player not in room");

        if (state.CurrentPlayerIndex != seat)
            return GameActionResult.Error("Not your turn");

        if (state.Status != (byte)LuckyMineStatus.Active)
            return GameActionResult.Error("Game ended");

        if (tileIndex < 0 || tileIndex >= state.TotalTiles)
            return GameActionResult.Error("Invalid tile index");

        if (state.IsRevealed(tileIndex))
            return GameActionResult.Error("Tile already revealed");
        
        if (state.IsDead(seat))
            return GameActionResult.Error("You are eliminated");

        var events = new List<GameEvent>();

        state.SetRevealed(tileIndex);

        if (state.JackpotCounter > 0) state.JackpotCounter--;

        bool isMine = state.IsMine(tileIndex);
        long coinChange = 0;

        if (isMine)
        {
            state.SetDead(seat);
            events.Add(new GameEvent("PlayerEliminated", new { Player = seat, UserId = userId, Tile = tileIndex }));
            logger.LogInformation("Room {Room} Player {Player} hit mine at {Tile}", roomId, userId, tileIndex);
        }
        else
        {
            float risk = state.TotalMines;
            float gain = (risk * state.RewardSlope) + 1.0f;

            coinChange = (long)(state.EntryCost * gain);
            
            events.Add(new GameEvent("TileSafe", new { 
                Player = seat, 
                Tile = tileIndex, 
                WinAmount = coinChange,
                JackpotLeft = state.JackpotCounter 
            }));

            if (state.JackpotCounter == 0)
            {
                state.Status = (byte)LuckyMineStatus.JackpotHit;
                events.Add(new GameEvent("JackpotWon", new { Player = seat, Prize = "iPhone" })); 
            }
        }

        events.Add(new GameEvent("Transaction", new { UserId = userId, Amount = coinChange - state.EntryCost }));

        AdvanceTurn(ref state, ctx.Meta.CurrentPlayerCount);

        await _repository.SaveAsync(roomId, state, ctx.Meta);

        return new GameActionResult
        {
            Success = true,
            ShouldBroadcast = true,
            NewState = MapToDto(state),
            Events = events
        };
    }

    private void AdvanceTurn(ref LuckyMineState state, int playerCount)
    {
        if (playerCount == 0) return;
        
        int attempts = 0;
        do
        {
            state.CurrentPlayerIndex = (state.CurrentPlayerIndex + 1) % playerCount;
            attempts++;
        } while (state.IsDead(state.CurrentPlayerIndex) && attempts < playerCount);
    }

    public async Task<IReadOnlyList<string>> GetLegalActionsAsync(string roomId, string userId)
    {
        var ctx = await _repository.LoadAsync(roomId);
        if (ctx == null) return [];

        if (ctx.Meta.PlayerSeats.TryGetValue(userId, out var seat) && 
            ctx.State.CurrentPlayerIndex == seat && 
            !ctx.State.IsDead(seat))
        {
            return ["click"];
        }
        return [];
    }

    public async Task<GameStateResponse?> GetStateAsync(string roomId)
    {
        var ctx = await _repository.LoadAsync(roomId);
        if (ctx == null) return null;

        return new GameStateResponse
        {
            RoomId = roomId,
            GameType = GameType,
            Meta = ctx.Meta,
            State = MapToDto(ctx.State),
            LegalMoves = ctx.State.CurrentPlayerIndex < ctx.Meta.CurrentPlayerCount ? ["click"] : []
        };
    }

    private LuckyMineDto MapToDto(LuckyMineState s) => new()
    {
        RevealedMask0 = s.RevealedMask0,
        RevealedMask1 = s.RevealedMask1,
        JackpotCounter = s.JackpotCounter,
        CurrentPlayerIndex = s.CurrentPlayerIndex,
        TotalTiles = s.TotalTiles,
        RemainingMines = s.TotalMines, 
        EntryCost = s.EntryCost,
        Status = ((LuckyMineStatus)s.Status).ToString()
    };
}