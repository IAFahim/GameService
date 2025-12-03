using GameService.GameCore;
using Microsoft.Extensions.Logging;

namespace GameService.LuckyMine;

public sealed class LuckyMineEngine(
    IGameRepositoryFactory repoFactory,
    ILogger<LuckyMineEngine> logger) : IGameEngine
{
    private readonly IGameRepository<LuckyMineState> _repository = repoFactory.Create<LuckyMineState>("LuckyMine");

    private static readonly string[] _legalClick = ["click"];
    private static readonly string[] _noActions = [];

    public string GameType => "LuckyMine";

    public async Task<GameActionResult> ExecuteAsync(string roomId, GameCommand command)
    {
        if (command.Action.AsSpan().Equals("click", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleClickAsync(roomId, command.UserId, command.GetInt("tileIndex"));
        }
        return GameActionResult.Error($"Unknown action: {command.Action}");
    }

    public async Task<IReadOnlyList<string>> GetLegalActionsAsync(string roomId, string userId)
    {
        var ctx = await _repository.LoadAsync(roomId);
        if (ctx == null) return _noActions;

        if (!ctx.Meta.PlayerSeats.TryGetValue(userId, out var seat)) return _noActions;
        
        // OPTIMIZATION: Copy state to local variable once
        var state = ctx.State; 

        if (state.CurrentPlayerIndex == seat && 
            !state.IsDead(seat) && 
            state.Status == (byte)LuckyMineStatus.Active)
        {
            return _legalClick;
        }
            
        return _noActions;
    }

    public async Task<GameStateResponse?> GetStateAsync(string roomId)
    {
        var ctx = await _repository.LoadAsync(roomId);
        if (ctx == null) return null;

        // FIX: Copy to local variable to pass by ref
        var state = ctx.State;

        return new GameStateResponse
        {
            RoomId = roomId,
            GameType = GameType,
            Meta = ctx.Meta,
            // FIX: Pass local variable
            State = MapToDto(ref state),
            LegalMoves = state.Status == (byte)LuckyMineStatus.Active ? _legalClick : _noActions
        };
    }

    private async Task<GameActionResult> HandleClickAsync(string roomId, string userId, int tileIndex)
    {
        var ctx = await _repository.LoadAsync(roomId);
        if (ctx == null) return GameActionResult.Error("Room not found");

        // FIX: Copy state to local variable for mutation
        var state = ctx.State;

        if (!ctx.Meta.PlayerSeats.TryGetValue(userId, out var seat)) return GameActionResult.Error("Player not in room");
        if (state.CurrentPlayerIndex != seat) return GameActionResult.Error("Not your turn");
        if (state.IsDead(seat)) return GameActionResult.Error("You are eliminated");
        if (state.Status != (byte)LuckyMineStatus.Active) return GameActionResult.Error("Game ended");

        if (tileIndex < 0 || tileIndex >= state.TotalTiles) return GameActionResult.Error("Invalid tile");
        if (state.IsRevealed(tileIndex)) return GameActionResult.Error("Tile already revealed");

        var events = new List<GameEvent>();
        
        // Mutate local state
        state.SetRevealed(tileIndex);

        bool isMine = state.IsMine(tileIndex);
        long winAmount = 0;

        if (isMine)
        {
            state.SetDead(seat);
            events.Add(new GameEvent("PlayerEliminated", new { Player = seat, UserId = userId, Tile = tileIndex }));
            logger.LogInformation("Room {Room} Player {Player} hit mine at {Tile}", roomId, userId, tileIndex);
        }
        else
        {
            double riskFactor = state.TotalMines;
            double gain = (riskFactor * state.RewardSlope) + 1.0;
            winAmount = (long)(state.EntryCost * gain);

            events.Add(new GameEvent("TileSafe", new
            {
                Player = seat,
                Tile = tileIndex,
                WinAmount = winAmount
            }));
        }

        long netChange = winAmount - state.EntryCost;
        events.Add(new GameEvent("Transaction", new { UserId = userId, Amount = netChange }));

        // Pass local variable by ref
        AdvanceTurn(ref state, ctx.Meta.CurrentPlayerCount);

        // Save the modified local state back to repository
        await _repository.SaveAsync(roomId, state, ctx.Meta);

        return new GameActionResult
        {
            Success = true,
            ShouldBroadcast = true,
            // Pass local variable by ref
            NewState = MapToDto(ref state),
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

    private LuckyMineDto MapToDto(ref LuckyMineState s)
    {
        return new LuckyMineDto
        {
            RevealedMask0 = s.RevealedMask0,
            RevealedMask1 = s.RevealedMask1,
            CurrentPlayerIndex = s.CurrentPlayerIndex,
            TotalTiles = s.TotalTiles,
            RemainingMines = s.TotalMines,
            EntryCost = s.EntryCost,
            Status = ((LuckyMineStatus)s.Status).ToString()
        };
    }
}