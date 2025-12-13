using System.Text.Json;
using GameService.Sdk.Core;

namespace GameService.Sdk.Ludo;

public sealed class LudoClient
{
    private readonly GameClient _client;
    private LudoState? _lastState;

    public int MySeat { get; private set; } = -1;

    public LudoState? State => _lastState;

    public bool IsMyTurn => _lastState?.CurrentPlayer == MySeat;

    public int LastDiceRoll => _lastState?.LastDiceRoll ?? 0;

    public bool IsGameOver => _lastState?.IsGameOver ?? false;

    public bool IsWaitingForPlayers => (_lastState?.ActiveSeatsMask ?? 0) == 0;

    private int _currentPlayerCount;
    private int _maxPlayers;

    public bool IsRoomFull => _currentPlayerCount >= _maxPlayers && _maxPlayers > 0;

    public event Action<int, int>? OnDiceRolled;

    public event Action<int, int, int, int>? OnTokenMoved;

    public event Action<int, int>? OnTokenCaptured;

    public event Action<int>? OnPlayerFinished;

    public event Action<int>? OnTurnChanged;


    public event Action<int>? OnTurnTimeout;

    public event Action<int[]>? OnGameEnded;

    public event Action<LudoState>? OnStateUpdated;

    public event Action<int, string, string>? OnPlayerJoined;

    public event Action<string, string>? OnPlayerLeft;

    public LudoClient(GameClient client)
    {
        _client = client;

        _client.OnGameState += HandleGameState;
        _client.OnGameEvent += HandleGameEvent;

        _client.OnPlayerJoined += p => OnPlayerJoined?.Invoke(p.SeatIndex, p.UserName, p.UserId);
        _client.OnPlayerLeft += p => OnPlayerLeft?.Invoke(p.UserId, p.UserName);
    }

    public async Task<CreateRoomResult> CreateGameAsync(string templateName = "StandardLudo")
    {
        var result = await _client.CreateRoomAsync(templateName);
        if (result.Success)
        {
            MySeat = 0;
            await _client.GetStateAsync();
        }
        return result;
    }

    public async Task<JoinRoomResult> JoinGameAsync(string roomId)
    {
        var result = await _client.JoinRoomAsync(roomId);
        if (result.Success)
        {
            MySeat = result.SeatIndex;
        }
        return result;
    }

    public Task LeaveGameAsync() => _client.LeaveRoomAsync();

    public async Task<DiceRollResult> RollDiceAsync()
    {
        var result = await _client.PerformActionAsync("Roll");
        
        if (!result.Success)
        {
            return new DiceRollResult(false, 0, false, Array.Empty<int>(), result.Error);
        }

        var state = ParseState(result.NewState);
        if (state == null)
        {
            return new DiceRollResult(false, 0, false, Array.Empty<int>(), "Failed to parse state");
        }

        var legalTokens = GetLegalTokenMoves(state);
        var canMove = legalTokens.Length > 0;

        return new DiceRollResult(true, state.LastDiceRoll, canMove, legalTokens, null);
    }

    public async Task<MoveResult> MoveTokenAsync(int tokenIndex)
    {
        if (tokenIndex < 0 || tokenIndex > 3)
        {
            return new MoveResult(false, "Token index must be 0-3");
        }

        var result = await _client.PerformActionAsync("Move", new { TokenIndex = tokenIndex });
        return new MoveResult(result.Success, result.Error);
    }

    public async Task<ActionResult> SkipTurnAsync()
    {
        return await _client.PerformActionAsync("Skip");
    }

    public int[] GetMyTokenPositions()
    {
        if (_lastState == null || MySeat < 0) return Array.Empty<int>();
        return GetPlayerTokens(_lastState, MySeat);
    }

    public int[] GetPlayerTokens(int seatIndex)
    {
        if (_lastState == null) return Array.Empty<int>();
        return GetPlayerTokens(_lastState, seatIndex);
    }

    public int[] GetMovableTokens()
    {
        if (_lastState == null) return Array.Empty<int>();
        return GetLegalTokenMoves(_lastState);
    }

    public int[] GetWinnerRanking()
    {
        if (_lastState == null) return Array.Empty<int>();
        return UnpackWinners(_lastState.WinnersPacked);
    }

    public bool HasPlayerFinished(int seatIndex)
    {
        if (_lastState == null || seatIndex < 0 || seatIndex > 3) return false;
        return (_lastState.FinishedMask & (1 << seatIndex)) != 0;
    }

    public bool IsSeatActive(int seatIndex)
    {
        if (_lastState == null || seatIndex < 0 || seatIndex > 3) return false;
        return (_lastState.ActiveSeatsMask & (1 << seatIndex)) != 0;
    }

    private void HandleGameState(GameState state)
    {
        if (state.GameType != "Ludo") return;

        _currentPlayerCount = state.PlayerCount;
        _maxPlayers = state.MaxPlayers;

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
                    var seat = GetInt(diceData, "player", "seat");
                    var value = GetInt(diceData, "value");
                    OnDiceRolled?.Invoke(seat, value);
                }
                break;

            case "TokenMoved":
                if (evt.Data is JsonElement moveData)
                {
                    var seat = GetInt(moveData, "player", "seat");
                    var token = GetInt(moveData, "tokenIndex", "token");
                    var to = GetInt(moveData, "newPosition", "to");
                    
                    var from = -1;
                    if (_lastState != null)
                    {
                        var tokens = GetPlayerTokens(_lastState, seat);
                        if (token >= 0 && token < tokens.Length) from = tokens[token];
                    }

                    OnTokenMoved?.Invoke(seat, token, from, to);
                }
                break;

            case "TokenCaptured":
                if (evt.Data is JsonElement captureData)
                {
                    var seat = GetInt(captureData, "capturedPlayer", "seat");
                    var token = GetInt(captureData, "capturedToken", "token");
                    OnTokenCaptured?.Invoke(seat, token);
                }
                break;

            case "PlayerFinished":
                if (evt.Data is JsonElement finishData)
                {
                    var seat = GetInt(finishData, "player", "seat");
                    OnPlayerFinished?.Invoke(seat);
                }
                break;

            case "TurnTimeout":
                if (evt.Data is JsonElement timeoutData)
                {
                    var player = GetInt(timeoutData, "player");
                    OnTurnTimeout?.Invoke(player);
                }
                break;
        }
    }

    private static int GetInt(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            if (el.TryGetProperty(name, out var prop) || 
                el.TryGetProperty(char.ToLowerInvariant(name[0]) + name.Substring(1), out prop))
            {
                if (prop.ValueKind == JsonValueKind.Number) return prop.GetInt32();
            }
        }
        return 0;
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

public sealed record DiceRollResult(
    bool Success,
    int Value,
    bool CanMove,
    int[] MovableTokens,
    string? Error);

public sealed record MoveResult(bool Success, string? Error);