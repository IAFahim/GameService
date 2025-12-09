using GameService.GameCore;
using Microsoft.Extensions.Logging;

namespace GameService.LuckyMine;

public sealed class LuckyMineEngine(
    IGameRepositoryFactory repoFactory,
    ILogger<LuckyMineEngine> logger) : IGameEngine
{
    private static readonly string[] _legalActions = ["click", "cashout"];
    private static readonly string[] _noActions = [];
    private readonly IGameRepository<LuckyMineState> _repository = repoFactory.Create<LuckyMineState>("LuckyMine");

    public string GameType => "LuckyMine";

    public async Task<GameActionResult> ExecuteAsync(string roomId, GameCommand command)
    {
        if (!await _repository.TryAcquireLockAsync(roomId, TimeSpan.FromSeconds(2)))
            return GameActionResult.Error("System busy, please try again");

        try
        {
            var action = command.Action.AsSpan();
            if (action.Equals("click", StringComparison.OrdinalIgnoreCase) || action.Equals("Reveal", StringComparison.OrdinalIgnoreCase))
                return await HandleClickAsync(roomId, command.UserId, command.GetInt("tileIndex"));
            if (action.Equals("cashout", StringComparison.OrdinalIgnoreCase))
                return await HandleCashoutAsync(roomId, command.UserId);
            return GameActionResult.Error($"Unknown action: {command.Action}");
        }
        finally
        {
            await _repository.ReleaseLockAsync(roomId);
        }
    }

    public async Task<IReadOnlyList<string>> GetLegalActionsAsync(string roomId, string userId)
    {
        var ctx = await _repository.LoadAsync(roomId);
        if (ctx == null) return _noActions;

        if (!ctx.Meta.PlayerSeats.ContainsKey(userId)) return _noActions;

        var state = ctx.State;

        if (state.Status == (byte)LuckyMineStatus.Active) return _legalActions;

        return _noActions;
    }

    public async Task<GameStateResponse?> GetStateAsync(string roomId)
    {
        var ctx = await _repository.LoadAsync(roomId);
        if (ctx == null) return null;

        var state = ctx.State;

        return new GameStateResponse
        {
            RoomId = roomId,
            GameType = GameType,
            Meta = ctx.Meta,
            State = MapToDto(ref state),
            LegalMoves = state.Status == (byte)LuckyMineStatus.Active ? _legalActions : _noActions
        };
    }

    public async Task<IReadOnlyList<GameStateResponse>> GetManyStatesAsync(IReadOnlyList<string> roomIds)
    {
        if (roomIds.Count == 0) return [];
        
        var contexts = await _repository.LoadManyAsync(roomIds);
        var results = new List<GameStateResponse>(contexts.Count);
        
        foreach (var ctx in contexts)
        {
            var state = ctx.State;
            results.Add(new GameStateResponse
            {
                RoomId = ctx.RoomId,
                GameType = GameType,
                Meta = ctx.Meta,
                State = MapToDto(ref state),
                LegalMoves = state.Status == (byte)LuckyMineStatus.Active ? _legalActions : _noActions
            });
        }
        
        return results;
    }

    private async Task<GameActionResult> HandleClickAsync(string roomId, string userId, int tileIndex)
    {
        var ctx = await _repository.LoadAsync(roomId);
        if (ctx == null) return GameActionResult.Error("Room not found");

        var state = ctx.State;

        if (!ctx.Meta.PlayerSeats.ContainsKey(userId)) return GameActionResult.Error("Player not in room");
        if (state.Status != (byte)LuckyMineStatus.Active) return GameActionResult.Error("Game ended");

        if (tileIndex < 0 || tileIndex >= state.TotalTiles) return GameActionResult.Error("Invalid tile");
        if (state.IsRevealed(tileIndex)) return GameActionResult.Error("Tile already revealed");

        var events = new List<GameEvent>();

        state.SetRevealed(tileIndex);

        var isMine = state.IsMine(tileIndex);

        if (isMine)
        {
            state.Status = (byte)LuckyMineStatus.HitMine;
            state.CurrentWinnings = 0;
            events.Add(
                new GameEvent("HitMine", new { UserId = userId, Tile = tileIndex, LostAmount = state.EntryCost }));
            events.Add(new GameEvent("GameOver", new { UserId = userId, Result = "Lost", FinalWinnings = 0 }));
            logger.LogInformation("Room {Room} Player {Player} hit mine at {Tile}", roomId, userId, tileIndex);

            await _repository.SaveAsync(roomId, state, ctx.Meta);

            var response = new GameStateResponse
            {
                RoomId = roomId,
                GameType = GameType,
                Meta = ctx.Meta,
                State = MapToDto(ref state),
                LegalMoves = _noActions
            };

            return GameActionResult.GameOver(
                response,
                new GameEndedInfo(
                    roomId,
                    GameType,
                    ctx.Meta.PlayerSeats,
                    null, ctx.Meta.EntryFee,
                    ctx.Meta.TurnStartedAt),
                events.ToArray());
        }

        state.RevealedSafeCount++;
        state.CurrentWinnings = CalculateWinnings(ref state);

        events.Add(new GameEvent("TileSafe", new
        {
            Tile = tileIndex,
            RevealedCount = state.RevealedSafeCount,
            state.CurrentWinnings,
            NextTileWinnings = CalculateNextWinnings(ref state)
        }));

        await _repository.SaveAsync(roomId, state, ctx.Meta);

        var activeResponse = new GameStateResponse
        {
            RoomId = roomId,
            GameType = GameType,
            Meta = ctx.Meta,
            State = MapToDto(ref state),
            LegalMoves = _legalActions
        };

        return new GameActionResult
        {
            Success = true,
            ShouldBroadcast = true,
            NewState = activeResponse,
            Events = events
        };
    }

    private async Task<GameActionResult> HandleCashoutAsync(string roomId, string userId)
    {
        var ctx = await _repository.LoadAsync(roomId);
        if (ctx == null) return GameActionResult.Error("Room not found");

        var state = ctx.State;

        if (!ctx.Meta.PlayerSeats.ContainsKey(userId)) return GameActionResult.Error("Player not in room");
        if (state.Status != (byte)LuckyMineStatus.Active) return GameActionResult.Error("Game already ended");
        if (state.RevealedSafeCount == 0)
            return GameActionResult.Error("Must reveal at least one tile before cashing out");

        state.Status = (byte)LuckyMineStatus.CashedOut;
        var winnings = state.CurrentWinnings;

        var events = new List<GameEvent>
        {
            new("CashedOut", new { UserId = userId, Winnings = winnings }),
            new("GameOver", new { UserId = userId, Result = "Won", FinalWinnings = winnings }),
            new("Transaction", new { UserId = userId, Amount = winnings })
        };

        logger.LogInformation("Room {Room} Player {Player} cashed out with {Winnings}", roomId, userId, winnings);

        await _repository.SaveAsync(roomId, state, ctx.Meta);

        var cashoutResponse = new GameStateResponse
        {
            RoomId = roomId,
            GameType = GameType,
            Meta = ctx.Meta,
            State = MapToDto(ref state),
            LegalMoves = _noActions
        };

        return GameActionResult.GameOver(
            cashoutResponse,
            new GameEndedInfo(
                roomId,
                GameType,
                ctx.Meta.PlayerSeats,
                userId, ctx.Meta.EntryFee,
                ctx.Meta.TurnStartedAt,
                new[] { userId }), events.ToArray());
    }

    private long CalculateWinnings(ref LuckyMineState state)
    {
        var safeTiles = state.TotalTiles - state.TotalMines;
        if (safeTiles <= 0 || state.RevealedSafeCount <= 0) return 0;

        var multiplier = 1.0;
        var remaining = safeTiles;
        int total = state.TotalTiles;

        for (var i = 0; i < state.RevealedSafeCount; i++)
        {
            if (remaining <= 0) return 0; // Prevent division by zero
            multiplier *= (double)total / remaining;
            remaining--;
            total--;
        }

        return (long)(state.EntryCost * multiplier * 0.97);
    }

    private long CalculateNextWinnings(ref LuckyMineState state)
    {
        if (state.RevealedSafeCount >= state.TotalTiles - state.TotalMines) return 0;
        var tempState = state;
        tempState.RevealedSafeCount++;
        return CalculateWinnings(ref tempState);
    }

    private LuckyMineDto MapToDto(ref LuckyMineState s)
    {
        return new LuckyMineDto
        {
            RevealedMask0 = s.RevealedMask0,
            RevealedMask1 = s.RevealedMask1,
            TotalTiles = s.TotalTiles,
            TotalMines = s.TotalMines,
            RevealedSafeCount = s.RevealedSafeCount,
            EntryCost = s.EntryCost,
            CurrentWinnings = s.CurrentWinnings,
            NextTileWinnings = CalculateNextWinnings(ref s),
            Status = ((LuckyMineStatus)s.Status).ToString()
        };
    }
}