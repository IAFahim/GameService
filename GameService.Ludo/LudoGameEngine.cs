using System.Text.Json;
using GameService.GameCore;
using Microsoft.Extensions.Logging;

namespace GameService.Ludo;

/// <summary>
/// Ludo game engine implementing the unified IGameEngine interface.
/// Handles all game actions through the command pattern.
/// </summary>
public sealed class LudoGameEngine : IGameEngine
{
    private readonly IGameRepository<LudoState> _repository;
    private readonly IRoomRegistry _roomRegistry;
    private readonly IDiceRoller _diceRoller;
    private readonly ILogger<LudoGameEngine> _logger;

    public string GameType => "Ludo";

    public LudoGameEngine(
        IGameRepositoryFactory repositoryFactory,
        IRoomRegistry roomRegistry,
        ILogger<LudoGameEngine> logger)
    {
        _repository = repositoryFactory.Create<LudoState>(GameType);
        _roomRegistry = roomRegistry;
        _diceRoller = new ServerDiceRoller();
        _logger = logger;
    }

    public async Task<GameActionResult> ExecuteAsync(string roomId, GameCommand command)
    {
        return command.Action.ToLowerInvariant() switch
        {
            "roll" => await HandleRollAsync(roomId, command.UserId),
            "move" => await HandleMoveAsync(roomId, command.UserId, command.GetInt("tokenIndex")),
            _ => GameActionResult.Error($"Unknown action: {command.Action}")
        };
    }

    public async Task<IReadOnlyList<string>> GetLegalActionsAsync(string roomId, string userId)
    {
        var ctx = await _repository.LoadAsync(roomId);
        if (ctx == null) return [];

        var actions = new List<string>();

        if (!ctx.Meta.PlayerSeats.TryGetValue(userId, out var seatIndex))
            return [];
            
        if (ctx.State.CurrentPlayer != seatIndex)
            return [];

        if (ctx.State.LastDiceRoll == 0)
        {
            actions.Add("roll");
        }
        else
        {
            var engine = new LudoEngine(ctx.State, _diceRoller);
            var moves = engine.GetLegalMoves();
            foreach (var tokenIndex in moves)
            {
                actions.Add($"move:{tokenIndex}");
            }
        }

        return actions;
    }

    public async Task<GameStateResponse?> GetStateAsync(string roomId)
    {
        var ctx = await _repository.LoadAsync(roomId);
        if (ctx == null) return null;

        var engine = new LudoEngine(ctx.State, _diceRoller);
        var legalMoves = engine.GetLegalMoves();

        var tokenArray = new byte[16];
        for (int i = 0; i < 16; i++)
        {
            tokenArray[i] = ctx.State.Tokens[i];
        }

        return new GameStateResponse
        {
            RoomId = roomId,
            GameType = GameType,
            Meta = ctx.Meta,
            State = new LudoStateDto
            {
                CurrentPlayer = ctx.State.CurrentPlayer,
                LastDiceRoll = ctx.State.LastDiceRoll,
                TurnId = ctx.State.TurnId,
                Winner = ctx.State.Winner == 255 ? -1 : ctx.State.Winner,
                ActiveSeatsBinary = Convert.ToString(ctx.State.ActiveSeats, 2).PadLeft(4, '0'),
                Tokens = tokenArray,
                ConsecutiveSixes = ctx.State.ConsecutiveSixes
            },
            LegalMoves = legalMoves.Select(i => $"move:{i}").ToList()
        };
    }

    private async Task<GameActionResult> HandleRollAsync(string roomId, string userId)
    {
        var ctx = await _repository.LoadAsync(roomId);
        if (ctx == null)
            return GameActionResult.Error("Room not found");

        int seatIndex;

        if (userId == "ADMIN")
        {
            seatIndex = ctx.State.CurrentPlayer;
        }
        else
        {
            if (!ctx.Meta.PlayerSeats.TryGetValue(userId, out seatIndex))
                return GameActionResult.Error("Player not in room");

            if (ctx.State.CurrentPlayer != seatIndex)
                return GameActionResult.Error($"Not your turn. Waiting for seat {ctx.State.CurrentPlayer}");
        }

        var engine = new LudoEngine(ctx.State, _diceRoller);

        if (!engine.TryRollDice(out var result))
            return GameActionResult.Error($"Cannot roll: {result.Status}");

        await _repository.SaveAsync(roomId, engine.State, ctx.Meta);

        var events = new List<GameEvent>
        {
            new("DiceRolled", new { Value = result.DiceValue, Player = seatIndex })
        };

        if (result.Status == LudoStatus.TurnPassed || result.Status == LudoStatus.ForfeitTurn)
        {
            events.Add(new("TurnChanged", new { NewPlayer = engine.State.CurrentPlayer }));
        }

        var state = await GetStateAsync(roomId);
        
        _logger.LogInformation("Player {UserId} rolled {Dice} in room {RoomId}", userId, result.DiceValue, roomId);

        return new GameActionResult
        {
            Success = true,
            ShouldBroadcast = true,
            NewState = state,
            Events = events
        };
    }

    private async Task<GameActionResult> HandleMoveAsync(string roomId, string userId, int tokenIndex)
    {
        var ctx = await _repository.LoadAsync(roomId);
        if (ctx == null)
            return GameActionResult.Error("Room not found");

        int seatIndex;

        if (userId == "ADMIN")
        {
            seatIndex = ctx.State.CurrentPlayer;
        }
        else
        {
            if (!ctx.Meta.PlayerSeats.TryGetValue(userId, out seatIndex))
                return GameActionResult.Error("Player not in room");

            if (ctx.State.CurrentPlayer != seatIndex)
                return GameActionResult.Error("Not your turn");
        }

        var engine = new LudoEngine(ctx.State, _diceRoller);

        if (!engine.TryMoveToken(tokenIndex, out var result))
            return GameActionResult.Error($"Cannot move token: {result.Status}");

        await _repository.SaveAsync(roomId, engine.State, ctx.Meta);

        var events = new List<GameEvent>
        {
            new("TokenMoved", new 
            { 
                Player = seatIndex, 
                TokenIndex = tokenIndex, 
                NewPosition = result.NewPos 
            })
        };

        if ((result.Status & LudoStatus.CapturedOpponent) != 0)
        {
            events.Add(new("TokenCaptured", new 
            { 
                CapturedPlayer = result.CapturedPid, 
                CapturedToken = result.CapturedTid 
            }));
        }

        if ((result.Status & LudoStatus.GameWon) != 0)
        {
            events.Add(new("GameWon", new { Winner = seatIndex, WinnerUserId = userId }));
        }

        if ((result.Status & LudoStatus.ExtraTurn) == 0)
        {
            events.Add(new("TurnChanged", new { NewPlayer = engine.State.CurrentPlayer }));
        }

        var state = await GetStateAsync(roomId);

        _logger.LogInformation("Player {UserId} moved token {Token} to {Position} in room {RoomId}", 
            userId, tokenIndex, result.NewPos, roomId);

        return new GameActionResult
        {
            Success = true,
            ShouldBroadcast = true,
            NewState = state,
            Events = events
        };
    }
}

/// <summary>
/// DTO for Ludo game state (JSON-serializable)
/// </summary>
public sealed record LudoStateDto
{
    public int CurrentPlayer { get; init; }
    public int LastDiceRoll { get; init; }
    public int TurnId { get; init; }
    public int Winner { get; init; }
    public string ActiveSeatsBinary { get; init; } = "";
    public byte[] Tokens { get; init; } = [];
    public int ConsecutiveSixes { get; init; }
}
