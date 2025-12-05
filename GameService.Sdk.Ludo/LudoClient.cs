using System.Text.Json;
using GameService.Sdk.Core;

namespace GameService.Sdk.Ludo;

/// <summary>
/// 🎲 Ludo game client - the classic board game!
/// 
/// Quick start:
/// <code>
/// // Wrap your GameClient with Ludo-specific functionality
/// var ludo = new LudoClient(gameClient);
/// 
/// // Subscribe to game events
/// ludo.OnDiceRolled += (player, value) => Console.WriteLine($"{player} rolled {value}!");
/// ludo.OnTokenMoved += (player, token, from, to) => Console.WriteLine($"Token moved!");
/// ludo.OnTurnChanged += player => Console.WriteLine($"It's {player}'s turn!");
/// 
/// // Create or join a game
/// await ludo.CreateGameAsync();  // Creates a standard 4-player game
/// // Or: await ludo.JoinGameAsync("ABC123");
/// 
/// // Play!
/// var roll = await ludo.RollDiceAsync();
/// if (roll.CanMove)
/// {
///     await ludo.MoveTokenAsync(0);  // Move token 0
/// }
/// </code>
/// </summary>
public sealed class LudoClient
{
    private readonly GameClient _client;
    private LudoState? _lastState;

    /// <summary>Your seat number (0-3), or -1 if not seated</summary>
    public int MySeat { get; private set; } = -1;

    /// <summary>Current game state</summary>
    public LudoState? State => _lastState;

    /// <summary>Whether it's currently your turn</summary>
    public bool IsMyTurn => _lastState?.CurrentPlayer == MySeat;

    /// <summary>The last dice value that was rolled</summary>
    public int LastDiceRoll => _lastState?.LastDiceRoll ?? 0;

    /// <summary>Whether the game has ended</summary>
    public bool IsGameOver => _lastState?.IsGameOver ?? false;

    // ═══════════════════════════════════════════════════════════════
    // 🎯 LUDO-SPECIFIC EVENTS
    // ═══════════════════════════════════════════════════════════════

    /// <summary>🎲 A player rolled the dice</summary>
    public event Action<int, int>? OnDiceRolled;  // (seatIndex, value)

    /// <summary>🏃 A token was moved</summary>
    public event Action<int, int, int, int>? OnTokenMoved;  // (seatIndex, tokenIndex, fromPos, toPos)

    /// <summary>💥 A token was captured and sent home</summary>
    public event Action<int, int>? OnTokenCaptured;  // (capturedPlayerSeat, tokenIndex)

    /// <summary>🏠 A token reached home (finished)</summary>
    public event Action<int, int>? OnTokenFinished;  // (seatIndex, tokenIndex)

    /// <summary>🔄 Turn changed to a different player</summary>
    public event Action<int>? OnTurnChanged;  // (newPlayerSeat)

    /// <summary>🏆 Game ended with a winner ranking</summary>
    public event Action<int[]>? OnGameEnded;  // (winnerRanking - seats in order of finishing)

    /// <summary>📊 Game state updated</summary>
    public event Action<LudoState>? OnStateUpdated;

    public LudoClient(GameClient client)
    {
        _client = client;
        
        // Subscribe to core events and translate to Ludo-specific events
        _client.OnGameState += HandleGameState;
        _client.OnGameEvent += HandleGameEvent;
    }

    // ═══════════════════════════════════════════════════════════════
    // 🏠 ROOM MANAGEMENT
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 🎮 Create a new Ludo game
    /// </summary>
    /// <param name="templateName">Template name (default: "StandardLudo")</param>
    public async Task<CreateRoomResult> CreateGameAsync(string templateName = "StandardLudo")
    {
        var result = await _client.CreateRoomAsync(templateName);
        if (result.Success)
        {
            MySeat = 0; // Creator is always seat 0
        }
        return result;
    }

    /// <summary>
    /// 🚪 Join an existing Ludo game
    /// </summary>
    public async Task<JoinRoomResult> JoinGameAsync(string roomId)
    {
        var result = await _client.JoinRoomAsync(roomId);
        if (result.Success)
        {
            MySeat = result.SeatIndex;
        }
        return result;
    }

    /// <summary>
    /// 👋 Leave the current game
    /// </summary>
    public Task LeaveGameAsync() => _client.LeaveRoomAsync();

    // ═══════════════════════════════════════════════════════════════
    // 🎲 GAME ACTIONS
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 🎲 Roll the dice!
    /// </summary>
    /// <returns>The dice result with available moves</returns>
    public async Task<DiceRollResult> RollDiceAsync()
    {
        var result = await _client.PerformActionAsync("Roll");
        
        if (!result.Success)
        {
            return new DiceRollResult(false, 0, false, Array.Empty<int>(), result.Error);
        }

        // Parse the new state to get dice value and legal moves
        var state = ParseState(result.NewState);
        if (state == null)
        {
            return new DiceRollResult(false, 0, false, Array.Empty<int>(), "Failed to parse state");
        }

        var legalTokens = GetLegalTokenMoves(state);
        var canMove = legalTokens.Length > 0;

        return new DiceRollResult(true, state.LastDiceRoll, canMove, legalTokens, null);
    }

    /// <summary>
    /// 🏃 Move a token
    /// </summary>
    /// <param name="tokenIndex">Token to move (0-3)</param>
    public async Task<MoveResult> MoveTokenAsync(int tokenIndex)
    {
        if (tokenIndex < 0 || tokenIndex > 3)
        {
            return new MoveResult(false, "Token index must be 0-3");
        }

        var result = await _client.PerformActionAsync("Move", new { TokenIndex = tokenIndex });
        return new MoveResult(result.Success, result.Error);
    }

    /// <summary>
    /// ⏭️ Skip your turn (when you have no legal moves)
    /// </summary>
    public async Task<ActionResult> SkipTurnAsync()
    {
        return await _client.PerformActionAsync("Skip");
    }

    // ═══════════════════════════════════════════════════════════════
    // 📊 STATE HELPERS
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 🎯 Get all your token positions
    /// </summary>
    public int[] GetMyTokenPositions()
    {
        if (_lastState == null || MySeat < 0) return Array.Empty<int>();
        return GetPlayerTokens(_lastState, MySeat);
    }

    /// <summary>
    /// 📍 Get a specific player's token positions
    /// </summary>
    public int[] GetPlayerTokens(int seatIndex)
    {
        if (_lastState == null) return Array.Empty<int>();
        return GetPlayerTokens(_lastState, seatIndex);
    }

    /// <summary>
    /// 🎲 Get tokens that can legally move with the current dice roll
    /// </summary>
    public int[] GetMovableTokens()
    {
        if (_lastState == null) return Array.Empty<int>();
        return GetLegalTokenMoves(_lastState);
    }

    /// <summary>
    /// 🏆 Get the winner ranking (finished players in order)
    /// </summary>
    public int[] GetWinnerRanking()
    {
        if (_lastState == null) return Array.Empty<int>();
        return UnpackWinners(_lastState.WinnersPacked);
    }

    /// <summary>
    /// ✅ Check if a player has finished (all tokens home)
    /// </summary>
    public bool HasPlayerFinished(int seatIndex)
    {
        if (_lastState == null || seatIndex < 0 || seatIndex > 3) return false;
        return (_lastState.FinishedMask & (1 << seatIndex)) != 0;
    }

    /// <summary>
    /// 👥 Check if a seat is occupied by a player
    /// </summary>
    public bool IsSeatActive(int seatIndex)
    {
        if (_lastState == null || seatIndex < 0 || seatIndex > 3) return false;
        return (_lastState.ActiveSeatsMask & (1 << seatIndex)) != 0;
    }

    // ═══════════════════════════════════════════════════════════════
    // 🔧 INTERNAL HELPERS
    // ═══════════════════════════════════════════════════════════════

    private void HandleGameState(GameState state)
    {
        if (state.GameType != "Ludo") return;

        var ludoState = ParseState(state.GameData);
        if (ludoState == null) return;

        var previousPlayer = _lastState?.CurrentPlayer ?? -1;
        _lastState = ludoState;

        OnStateUpdated?.Invoke(ludoState);

        if (ludoState.CurrentPlayer != previousPlayer)
        {
            OnTurnChanged?.Invoke(ludoState.CurrentPlayer);
        }

        if (ludoState.IsGameOver)
        {
            OnGameEnded?.Invoke(UnpackWinners(ludoState.WinnersPacked));
        }
    }

    private void HandleGameEvent(GameEvent evt)
    {
        switch (evt.EventName)
        {
            case "DiceRolled":
                if (evt.Data is JsonElement diceData)
                {
                    var seat = diceData.GetProperty("seat").GetInt32();
                    var value = diceData.GetProperty("value").GetInt32();
                    OnDiceRolled?.Invoke(seat, value);
                }
                break;

            case "TokenMoved":
                if (evt.Data is JsonElement moveData)
                {
                    var seat = moveData.GetProperty("seat").GetInt32();
                    var token = moveData.GetProperty("token").GetInt32();
                    var from = moveData.GetProperty("from").GetInt32();
                    var to = moveData.GetProperty("to").GetInt32();
                    OnTokenMoved?.Invoke(seat, token, from, to);
                }
                break;

            case "TokenCaptured":
                if (evt.Data is JsonElement captureData)
                {
                    var seat = captureData.GetProperty("seat").GetInt32();
                    var token = captureData.GetProperty("token").GetInt32();
                    OnTokenCaptured?.Invoke(seat, token);
                }
                break;

            case "TokenFinished":
                if (evt.Data is JsonElement finishData)
                {
                    var seat = finishData.GetProperty("seat").GetInt32();
                    var token = finishData.GetProperty("token").GetInt32();
                    OnTokenFinished?.Invoke(seat, token);
                }
                break;
        }
    }

    private static LudoState? ParseState(object? data)
    {
        if (data == null) return null;

        try
        {
            if (data is JsonElement element)
            {
                return JsonSerializer.Deserialize<LudoState>(element.GetRawText());
            }
            var json = JsonSerializer.Serialize(data);
            return JsonSerializer.Deserialize<LudoState>(json);
        }
        catch
        {
            return null;
        }
    }

    private static int[] GetPlayerTokens(LudoState state, int seatIndex)
    {
        if (state.Tokens == null || state.Tokens.Length < (seatIndex + 1) * 4)
            return Array.Empty<int>();

        var result = new int[4];
        var baseIndex = seatIndex * 4;
        for (var i = 0; i < 4; i++)
        {
            result[i] = state.Tokens[baseIndex + i];
        }
        return result;
    }

    private static int[] GetLegalTokenMoves(LudoState state)
    {
        var legal = new List<int>();
        for (var i = 0; i < 4; i++)
        {
            if ((state.LegalMovesMask & (1 << i)) != 0)
            {
                legal.Add(i);
            }
        }
        return legal.ToArray();
    }

    private static int[] UnpackWinners(uint packed)
    {
        var winners = new List<int>();
        for (var i = 0; i < 4; i++)
        {
            var slot = (packed >> (i * 8)) & 0xFF;
            if (slot > 0 && slot <= 4)
            {
                winners.Add((int)slot - 1);
            }
        }
        return winners.ToArray();
    }
}

// ═══════════════════════════════════════════════════════════════
// 📦 LUDO TYPES
// ═══════════════════════════════════════════════════════════════

/// <summary>Ludo game state</summary>
public sealed class LudoState
{
    public int CurrentPlayer { get; set; }
    public int LastDiceRoll { get; set; }
    public int TurnId { get; set; }
    public int ConsecutiveSixes { get; set; }
    public DateTimeOffset TurnStartedAt { get; set; }
    public int TurnTimeoutSeconds { get; set; }
    public byte ActiveSeatsMask { get; set; }
    public byte FinishedMask { get; set; }
    public byte LegalMovesMask { get; set; }
    public uint WinnersPacked { get; set; }
    public bool IsGameOver { get; set; }
    public byte[]? Tokens { get; set; }
}

/// <summary>Result of rolling the dice</summary>
public sealed record DiceRollResult(
    bool Success,
    int Value,
    bool CanMove,
    int[] MovableTokens,
    string? Error);

/// <summary>Result of moving a token</summary>
public sealed record MoveResult(bool Success, string? Error);
