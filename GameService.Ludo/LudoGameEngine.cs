using System.Text.Json;
using GameService.GameCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameService.Ludo;

public static class LudoActions
{
    public const string Roll = "roll";
    public const string Move = "move";

    public static readonly string[] MoveActions = 
    [
        $"{Move}:0", 
        $"{Move}:1", 
        $"{Move}:2", 
        $"{Move}:3"
    ];
}

public static class LudoEvents
{
    public const string DiceRolled = "DiceRolled";
    public const string TokenMoved = "TokenMoved";
    public const string TokenCaptured = "TokenCaptured";
    public const string PlayerFinished = "PlayerFinished";
    public const string TurnChanged = "TurnChanged";
    public const string TurnTimeout = "TurnTimeout";
    public const string GameEnded = "GameEnded";
}

public sealed class LudoGameEngine : ITurnBasedGameEngine
{
    private static readonly string[] CachedMoveActions = LudoActions.MoveActions;
    private static readonly string[] CachedRollAction = [LudoActions.Roll];
    private static readonly string[] EmptyActions = [];
    private readonly IDiceRoller _diceRoller;
    private readonly ILogger<LudoGameEngine> _logger;
    private readonly IGameRepository<LudoState> _repository;

    public LudoGameEngine(
        IGameRepositoryFactory repositoryFactory,
        ILogger<LudoGameEngine> logger,
        IOptions<LudoOptions>? options = null)
    {
        _repository = repositoryFactory.Create<LudoState>(GameType);
        _diceRoller = new ServerDiceRoller();
        _logger = logger;
        TurnTimeoutSeconds = options?.Value.TurnTimeoutSeconds ?? 30;
    }

    public string GameType => "Ludo";
    public int TurnTimeoutSeconds { get; }

    public async Task<GameActionResult> ExecuteAsync(string roomId, GameCommand command)
    {
        try
        {
            var ctx = await _repository.LoadAsync(roomId);
            if (ctx == null) return GameActionResult.Error("Room not found");

            if (ctx.Meta.PlayerSeats.Count < ctx.Meta.MaxPlayers && command.UserId != GameCoreConstants.AdminUserId)
            {
                return GameActionResult.Error($"Waiting for players... ({ctx.Meta.PlayerSeats.Count}/{ctx.Meta.MaxPlayers})");
            }

            var actionSpan = command.Action.AsSpan();

            if (actionSpan.Equals(LudoActions.Roll, StringComparison.OrdinalIgnoreCase))
                return await HandleRollAsync(roomId, command.UserId, command.Payload);

            if (actionSpan.StartsWith(LudoActions.Move, StringComparison.OrdinalIgnoreCase))
                return await HandleMoveAsync(roomId, command.UserId, command.GetInt("tokenIndex"));

            return GameActionResult.Error($"Unknown action: {command.Action}");
        }
        catch (Exception ex)
        {
            return GameActionResult.Error($"Ludo Engine Error: {ex.GetType().Name} - {ex.Message}");
        }
    }

    public async Task<GameActionResult?> CheckTimeoutsAsync(string roomId)
    {
        var ctx = await _repository.LoadAsync(roomId);
        if (ctx == null) return null;

        var state = ctx.State;
        if (state.IsGameOver()) return null;

        var elapsed = DateTimeOffset.UtcNow - ctx.Meta.TurnStartedAt;
        if (elapsed.TotalSeconds < TurnTimeoutSeconds) return null;

        _logger.LogInformation("Timeout in room {RoomId} for player {Player}", roomId, state.CurrentPlayer);

        var events = new List<GameEvent> { new(LudoEvents.TurnTimeout, new { Player = state.CurrentPlayer }) };

        if (state.LastDiceRoll == 0)
            if (LudoEngine.TryRollDice(ref state, _diceRoller, out var res))
                events.Add(new GameEvent(LudoEvents.DiceRolled,
                    new { Value = res.DiceValue, Player = state.CurrentPlayer, AutoPlay = true }));

        int mask = LudoEngine.GetLegalMovesMask(ref state);
        if (mask != 0)
        {
            var tokenToMove = (mask & 1) != 0 ? 0 : (mask & 2) != 0 ? 1 : (mask & 4) != 0 ? 2 : 3;

            if (LudoEngine.TryMoveToken(ref state, tokenToMove, out var res))
            {
                events.Add(new GameEvent(LudoEvents.TokenMoved,
                    new
                    {
                        Player = ctx.State.CurrentPlayer, TokenIndex = tokenToMove, NewPosition = res.NewPos,
                        AutoPlay = true
                    }));
                ProcessEvents(events, res, ctx.State.CurrentPlayer);
            }
        }

        if (state.TurnId != ctx.State.TurnId || state.CurrentPlayer != ctx.State.CurrentPlayer)
            events.Add(new GameEvent(LudoEvents.TurnChanged, new { NewPlayer = state.CurrentPlayer }));

        var newMeta = ctx.Meta with { TurnStartedAt = DateTimeOffset.UtcNow };
        await _repository.SaveAsync(roomId, state, newMeta);

        var finalState = state;
        var response = new GameStateResponse
        {
            RoomId = roomId,
            GameType = GameType,
            Meta = newMeta,
            State = MapToDto(ref finalState, newMeta),
            LegalMoves = []
        };

        return new GameActionResult
        {
            Success = true,
            ShouldBroadcast = true,
            Events = events,
            NewState = response
        };
    }

    public async Task<IReadOnlyList<string>> GetLegalActionsAsync(string roomId, string userId)
    {
        var ctx = await _repository.LoadAsync(roomId);
        if (ctx == null) return EmptyActions;

        if (!ctx.Meta.PlayerSeats.TryGetValue(userId, out var seat)) return EmptyActions;
        if (ctx.State.CurrentPlayer != seat) return EmptyActions;

        if (ctx.State.LastDiceRoll == 0) return CachedRollAction;

        var state = ctx.State;
        int mask = LudoEngine.GetLegalMovesMask(ref state);

        if (mask == 0) return EmptyActions;

        var list = new List<string>(4);
        if ((mask & 1) != 0) list.Add(CachedMoveActions[0]);
        if ((mask & 2) != 0) list.Add(CachedMoveActions[1]);
        if ((mask & 4) != 0) list.Add(CachedMoveActions[2]);
        if ((mask & 8) != 0) list.Add(CachedMoveActions[3]);
        return list;
    }

    public async Task<GameStateResponse?> GetStateAsync(string roomId)
    {
        var ctx = await _repository.LoadAsync(roomId);
        if (ctx == null) return null;

        var localState = ctx.State;
        return new GameStateResponse
        {
            RoomId = roomId,
            GameType = GameType,
            Meta = ctx.Meta,
            State = MapToDto(ref localState, ctx.Meta),
            LegalMoves = []
        };
    }

    public async Task<IReadOnlyList<GameStateResponse>> GetManyStatesAsync(IReadOnlyList<string> roomIds)
    {
        if (roomIds.Count == 0) return [];
        
        var contexts = await _repository.LoadManyAsync(roomIds);
        var results = new List<GameStateResponse>(contexts.Count);
        
        foreach (var ctx in contexts)
        {
            var localState = ctx.State;
            results.Add(new GameStateResponse
            {
                RoomId = ctx.RoomId,
                GameType = GameType,
                Meta = ctx.Meta,
                State = MapToDto(ref localState, ctx.Meta),
                LegalMoves = []
            });
        }
        
        return results;
    }

    private async Task<GameActionResult> HandleRollAsync(string roomId, string userId, JsonElement? payload = null)
    {
        var ctx = await _repository.LoadAsync(roomId);
        if (ctx == null) return GameActionResult.Error("Room not found");
        if (!ValidateTurn(ctx, userId, out var seat)) return GameActionResult.Error("Not your turn");

        var state = ctx.State;
        
        byte? forcedDice = null;
        if (userId == GameCoreConstants.AdminUserId && payload.HasValue)
        {
            if (payload.Value.TryGetProperty("ForcedValue", out var prop))
            {
                forcedDice = (byte)prop.GetInt32();
            }
        }

        if (!LudoEngine.TryRollDice(ref state, _diceRoller, out var res, forcedDice)) return GameActionResult.Error($"Roll failed: {res.Status}");

        var events = new List<GameEvent> { new(LudoEvents.DiceRolled, new { Value = res.DiceValue, Player = seat }) };

        if (res.Status.HasFlag(LudoStatus.TurnPassed) || res.Status.HasFlag(LudoStatus.ForfeitTurn))
            events.Add(new GameEvent(LudoEvents.TurnChanged, new { NewPlayer = state.CurrentPlayer }));

        var newMeta = ctx.Meta with { TurnStartedAt = DateTimeOffset.UtcNow };
        await _repository.SaveAsync(roomId, state, newMeta);

        var finalState = state;
        var response = new GameStateResponse
        {
            RoomId = roomId,
            GameType = GameType,
            Meta = newMeta,
            State = MapToDto(ref finalState, newMeta),
            LegalMoves = []
        };
        return new GameActionResult
            { Success = true, ShouldBroadcast = true, Events = events, NewState = response };
    }

    private async Task<GameActionResult> HandleMoveAsync(string roomId, string userId, int tIdx)
    {
        var ctx = await _repository.LoadAsync(roomId);
        if (ctx == null) return GameActionResult.Error("Room not found");
        if (!ValidateTurn(ctx, userId, out var seat)) return GameActionResult.Error("Not your turn");

        var state = ctx.State;
        if (!LudoEngine.TryMoveToken(ref state, tIdx, out var res)) return GameActionResult.Error($"Move failed: {res.Status}");

        var events = new List<GameEvent>
            { new(LudoEvents.TokenMoved, new { Player = seat, TokenIndex = tIdx, NewPosition = res.NewPos }) };
        ProcessEvents(events, res, seat);

        if (!res.Status.HasFlag(LudoStatus.ExtraTurn) || res.Status.HasFlag(LudoStatus.ErrorGameEnded))
            events.Add(new GameEvent(LudoEvents.TurnChanged, new { NewPlayer = state.CurrentPlayer }));

        var newMeta = ctx.Meta with { TurnStartedAt = DateTimeOffset.UtcNow };
        await _repository.SaveAsync(roomId, state, newMeta);

        var finalState = state;
        var stateDto = MapToDto(ref finalState, newMeta);
        var response = new GameStateResponse
        {
            RoomId = roomId,
            GameType = GameType,
            Meta = newMeta,
            State = stateDto,
            LegalMoves = []
        };

        if (res.Status.HasFlag(LudoStatus.ErrorGameEnded))
        {
            var winnerRanking = GetWinnerRanking(ref finalState, ctx.Meta);
            var winnerUserId = winnerRanking.Count > 0 ? winnerRanking[0] : null;
            var totalPot = ctx.Meta.EntryFee * ctx.Meta.PlayerSeats.Count;

            return GameActionResult.GameOver(
                response,
                new GameEndedInfo(
                    roomId,
                    GameType,
                    ctx.Meta.PlayerSeats,
                    winnerUserId,
                    totalPot,
                    ctx.Meta.TurnStartedAt, winnerRanking),
                events.ToArray());
        }

        return new GameActionResult { Success = true, ShouldBroadcast = true, Events = events, NewState = response };
    }

    private List<string> GetWinnerRanking(ref LudoState state, GameRoomMeta meta)
    {
        var ranking = new List<string>();
        var seatToUser = meta.PlayerSeats.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);

        for (var i = 0; i < 4; i++)
        {
            var seatIndex = (int)state.Winners[i];
            if (seatIndex != 255 && seatToUser.TryGetValue(seatIndex, out var userId)) ranking.Add(userId);
        }

        return ranking;
    }

    private bool ValidateTurn(GameContext<LudoState> ctx, string userId, out int seat)
    {
        seat = -1;
        if (userId == GameCoreConstants.AdminUserId || userId == GameCoreConstants.SystemUserId)
        {
            seat = ctx.State.CurrentPlayer;
            return true;
        }

        return ctx.Meta.PlayerSeats.TryGetValue(userId, out seat) && seat == ctx.State.CurrentPlayer;
    }

    private void ProcessEvents(List<GameEvent> list, MoveResult res, int p)
    {
        if (res.Status.HasFlag(LudoStatus.CapturedOpponent))
            list.Add(new GameEvent(LudoEvents.TokenCaptured,
                new { CapturedPlayer = res.CapturedPid, CapturedToken = res.CapturedTid }));

        if (res.Status.HasFlag(LudoStatus.PlayerFinished))
            list.Add(new GameEvent(LudoEvents.PlayerFinished, new { Player = p }));

        if (res.Status.HasFlag(LudoStatus.ErrorGameEnded))
            list.Add(new GameEvent(LudoEvents.GameEnded, new { }));
    }

    private LudoStateDto MapToDto(ref LudoState s, GameRoomMeta m)
    {
        var tokens = new byte[16];
        ((ReadOnlySpan<byte>)s.Tokens).CopyTo(tokens);

        var wPacked = s.Winners[0] | ((uint)s.Winners[1] << 8) | ((uint)s.Winners[2] << 16) |
                      ((uint)s.Winners[3] << 24);

        return new LudoStateDto
        {
            CurrentPlayer = s.CurrentPlayer, LastDiceRoll = s.LastDiceRoll, TurnId = s.TurnId,
            ConsecutiveSixes = s.ConsecutiveSixes, TurnStartedAt = m.TurnStartedAt,
            TurnTimeoutSeconds = TurnTimeoutSeconds,
            Tokens = tokens, ActiveSeatsMask = s.ActiveSeats, FinishedMask = s.FinishedMask, WinnersPacked = wPacked,
            IsGameOver = s.IsGameOver(),
            LegalMovesMask = LudoEngine.GetLegalMovesMask(ref s)
        };
    }
}