using GameService.GameCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameService.Ludo;

public sealed class LudoGameEngine : ITurnBasedGameEngine
{
    private readonly IDiceRoller _diceRoller;
    private readonly ILogger<LudoGameEngine> _logger;
    private readonly IGameRepository<LudoState> _repository;
    private readonly int _turnTimeoutSeconds;

    private static readonly string[] CachedMoveActions = ["move:0", "move:1", "move:2", "move:3"];
    private static readonly string[] CachedRollAction = ["roll"];
    private static readonly string[] EmptyActions = [];

    public LudoGameEngine(
        IGameRepositoryFactory repositoryFactory,
        ILogger<LudoGameEngine> logger,
        IOptions<LudoOptions>? options = null)
    {
        _repository = repositoryFactory.Create<LudoState>("Ludo");
        _diceRoller = new ServerDiceRoller();
        _logger = logger;
        _turnTimeoutSeconds = options?.Value.TurnTimeoutSeconds ?? 30;
    }

    public string GameType => "Ludo";
    public int TurnTimeoutSeconds => _turnTimeoutSeconds;

    public async Task<GameActionResult> ExecuteAsync(string roomId, GameCommand command)
    {
        var actionSpan = command.Action.AsSpan();

        if (actionSpan.Equals("roll", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleRollAsync(roomId, command.UserId);
        }
        else if (actionSpan.StartsWith("move", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleMoveAsync(roomId, command.UserId, command.GetInt("tokenIndex"));
        }

        return GameActionResult.Error($"Unknown action: {command.Action}");
    }

    public async Task<GameActionResult?> CheckTimeoutsAsync(string roomId)
    {
        var ctx = await _repository.LoadAsync(roomId);
        if (ctx == null) return null;

        var engine = new LudoEngine(_diceRoller) { State = ctx.State };
        if (engine.State.IsGameOver()) return null;

        var elapsed = DateTimeOffset.UtcNow - ctx.Meta.TurnStartedAt;
        if (elapsed.TotalSeconds < TurnTimeoutSeconds) return null;

        _logger.LogInformation("Timeout in room {RoomId} for player {Player}", roomId, engine.State.CurrentPlayer);

        var events = new List<GameEvent> { new("TurnTimeout", new { Player = engine.State.CurrentPlayer }) };

        if (engine.State.LastDiceRoll == 0)
        {
            if (engine.TryRollDice(out var res))
                events.Add(new("DiceRolled", new { Value = res.DiceValue, Player = engine.State.CurrentPlayer, AutoPlay = true }));
        }

        int mask = engine.GetLegalMovesMask();
        if (mask != 0)
        {
            int tokenToMove = (mask & 1) != 0 ? 0 : (mask & 2) != 0 ? 1 : (mask & 4) != 0 ? 2 : 3;

            if (engine.TryMoveToken(tokenToMove, out var res))
            {
                events.Add(new("TokenMoved", new { Player = ctx.State.CurrentPlayer, TokenIndex = tokenToMove, NewPosition = res.NewPos, AutoPlay = true }));
                ProcessEvents(events, res, ctx.State.CurrentPlayer);
            }
        }
        
        if (engine.State.TurnId != ctx.State.TurnId || engine.State.CurrentPlayer != ctx.State.CurrentPlayer)
             events.Add(new("TurnChanged", new { NewPlayer = engine.State.CurrentPlayer }));

        var newMeta = ctx.Meta with { TurnStartedAt = DateTimeOffset.UtcNow };
        await _repository.SaveAsync(roomId, engine.State, newMeta);

        var finalState = engine.State;
        return new GameActionResult 
        { 
            Success = true, 
            ShouldBroadcast = true, 
            Events = events, 
            NewState = MapToDto(ref finalState, newMeta) 
        };
    }

    public async Task<IReadOnlyList<string>> GetLegalActionsAsync(string roomId, string userId)
    {
        var ctx = await _repository.LoadAsync(roomId);
        if (ctx == null) return EmptyActions;

        if (!ctx.Meta.PlayerSeats.TryGetValue(userId, out var seat)) return EmptyActions;
        if (ctx.State.CurrentPlayer != seat) return EmptyActions;

        if (ctx.State.LastDiceRoll == 0) return CachedRollAction;

        var engine = new LudoEngine(_diceRoller) { State = ctx.State };
        int mask = engine.GetLegalMovesMask();
        
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

    private async Task<GameActionResult> HandleRollAsync(string roomId, string userId)
    {
        var ctx = await _repository.LoadAsync(roomId);
        if (ctx == null) return GameActionResult.Error("Room not found");
        if (!ValidateTurn(ctx, userId, out var seat)) return GameActionResult.Error("Not your turn");

        var engine = new LudoEngine(_diceRoller) { State = ctx.State };
        if (!engine.TryRollDice(out var res)) return GameActionResult.Error($"Roll failed: {res.Status}");

        var events = new List<GameEvent> { new("DiceRolled", new { Value = res.DiceValue, Player = seat }) };
        
        if (res.Status.HasFlag(LudoStatus.TurnPassed) || res.Status.HasFlag(LudoStatus.ForfeitTurn))
            events.Add(new("TurnChanged", new { NewPlayer = engine.State.CurrentPlayer }));

        var newMeta = ctx.Meta with { TurnStartedAt = DateTimeOffset.UtcNow };
        await _repository.SaveAsync(roomId, engine.State, newMeta);

        var finalState = engine.State;
        return new GameActionResult { Success = true, ShouldBroadcast = true, Events = events, NewState = MapToDto(ref finalState, newMeta) };
    }

    private async Task<GameActionResult> HandleMoveAsync(string roomId, string userId, int tIdx)
    {
        var ctx = await _repository.LoadAsync(roomId);
        if (ctx == null) return GameActionResult.Error("Room not found");
        if (!ValidateTurn(ctx, userId, out var seat)) return GameActionResult.Error("Not your turn");

        var engine = new LudoEngine(_diceRoller) { State = ctx.State };
        if (!engine.TryMoveToken(tIdx, out var res)) return GameActionResult.Error($"Move failed: {res.Status}");

        var events = new List<GameEvent> { new("TokenMoved", new { Player = seat, TokenIndex = tIdx, NewPosition = res.NewPos }) };
        ProcessEvents(events, res, seat);
        
        if (!res.Status.HasFlag(LudoStatus.ExtraTurn) || res.Status.HasFlag(LudoStatus.ErrorGameEnded))
            events.Add(new("TurnChanged", new { NewPlayer = engine.State.CurrentPlayer }));

        var newMeta = ctx.Meta with { TurnStartedAt = DateTimeOffset.UtcNow };
        await _repository.SaveAsync(roomId, engine.State, newMeta);

        var finalState = engine.State;
        return new GameActionResult { Success = true, ShouldBroadcast = true, Events = events, NewState = MapToDto(ref finalState, newMeta) };
    }

    private bool ValidateTurn(GameContext<LudoState> ctx, string userId, out int seat)
    {
        seat = -1;
        if (userId == "ADMIN" || userId == "SYSTEM") { seat = ctx.State.CurrentPlayer; return true; }
        return ctx.Meta.PlayerSeats.TryGetValue(userId, out seat) && seat == ctx.State.CurrentPlayer;
    }

    private void ProcessEvents(List<GameEvent> list, MoveResult res, int p)
    {
        if (res.Status.HasFlag(LudoStatus.CapturedOpponent)) 
            list.Add(new("TokenCaptured", new { CapturedPlayer = res.CapturedPid, CapturedToken = res.CapturedTid }));
        
        if (res.Status.HasFlag(LudoStatus.PlayerFinished)) 
            list.Add(new("PlayerFinished", new { Player = p }));
        
        if (res.Status.HasFlag(LudoStatus.ErrorGameEnded)) 
            list.Add(new("GameEnded", new { }));
    }

    private LudoStateDto MapToDto(ref LudoState s, GameRoomMeta m)
    {
        var tokens = new byte[16];
        ((ReadOnlySpan<byte>)s.Tokens).CopyTo(tokens);
        
        uint wPacked = s.Winners[0] | ((uint)s.Winners[1] << 8) | ((uint)s.Winners[2] << 16) | ((uint)s.Winners[3] << 24);

        // Calculate legal moves mask without needing dice roller (it's only used for rolling, not move calculation)
        var engine = new LudoEngine(_diceRoller) { State = s };
        
        return new LudoStateDto
        {
            CurrentPlayer = s.CurrentPlayer, LastDiceRoll = s.LastDiceRoll, TurnId = s.TurnId,
            ConsecutiveSixes = s.ConsecutiveSixes, TurnStartedAt = m.TurnStartedAt, TurnTimeoutSeconds = TurnTimeoutSeconds,
            Tokens = tokens, ActiveSeatsMask = s.ActiveSeats, FinishedMask = s.FinishedMask, WinnersPacked = wPacked,
            IsGameOver = s.IsGameOver(),
            LegalMovesMask = engine.GetLegalMovesMask()
        };
    }
}