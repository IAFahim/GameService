using System.Text.Json;
using GameService.Sdk.Core;

namespace GameService.Sdk.LuckyMine;

/// <summary>
/// 💎 LuckyMine game client - reveal tiles, avoid mines, cash out!
/// 
/// Quick start:
/// <code>
/// var mines = new LuckyMineClient(gameClient);
/// 
/// // Subscribe to events
/// mines.OnTileRevealed += (index, isMine) => {
///     if (isMine) Console.WriteLine("💥 BOOM!");
///     else Console.WriteLine($"✨ Safe! Current winnings: {mines.CurrentWinnings}");
/// };
/// 
/// // Start a game
/// await mines.StartGameAsync("5Mines");  // 5 mines out of 25 tiles
/// 
/// // Play - reveal tiles until you want to stop
/// var result = await mines.RevealTileAsync(12);  // Reveal tile at index 12
/// if (result.IsMine)
/// {
///     Console.WriteLine("Game over! You hit a mine.");
/// }
/// else
/// {
///     // Keep going or cash out
///     var cashout = await mines.CashOutAsync();
///     Console.WriteLine($"You won {cashout.Amount} coins!");
/// }
/// </code>
/// </summary>
public sealed class LuckyMineClient
{
    private readonly GameClient _client;
    private LuckyMineState? _lastState;

    /// <summary>Current game state</summary>
    public LuckyMineState? State => _lastState;

    /// <summary>Current potential winnings</summary>
    public long CurrentWinnings => _lastState?.CurrentWinnings ?? 0;

    /// <summary>Number of safe tiles revealed so far</summary>
    public int RevealedCount => _lastState?.RevealedSafeCount ?? 0;

    /// <summary>Total number of mines in this game</summary>
    public int TotalMines => _lastState?.TotalMines ?? 0;

    /// <summary>Total tiles on the board</summary>
    public int TotalTiles => _lastState?.TotalTiles ?? 25;

    /// <summary>Game status (Active, HitMine, CashedOut)</summary>
    public LuckyMineStatus Status => _lastState?.Status ?? LuckyMineStatus.Active;

    /// <summary>Whether the game is still active</summary>
    public bool IsActive => Status == LuckyMineStatus.Active;

    /// <summary>Whether you hit a mine</summary>
    public bool HitMine => Status == LuckyMineStatus.HitMine;

    /// <summary>Whether you successfully cashed out</summary>
    public bool CashedOut => Status == LuckyMineStatus.CashedOut;

    /// <summary>💎 A tile was revealed (safe or mine)</summary>
    public event Action<int, bool>? OnTileRevealed;

    /// <summary>💰 Player cashed out successfully</summary>
    public event Action<long>? OnCashedOut;

    /// <summary>💥 Player hit a mine - game over!</summary>
    public event Action? OnMineHit;

    /// <summary>🎮 New game started</summary>
    public event Action<int, int>? OnGameStarted;

    /// <summary>📊 State updated</summary>
    public event Action<LuckyMineState>? OnStateUpdated;

    public LuckyMineClient(GameClient client)
    {
        _client = client;
        _client.OnGameState += HandleGameState;
        _client.OnGameEvent += HandleGameEvent;
    }

    /// <summary>
    /// 🎮 Start a new LuckyMine game
    /// </summary>
    /// <param name="templateName">Template name (e.g., "3Mines", "5Mines", "10Mines", "24Mines")</param>
    public async Task<CreateRoomResult> StartGameAsync(string templateName = "5Mines")
    {
        return await _client.CreateRoomAsync(templateName);
    }

    /// <summary>
    /// 💎 Reveal a tile
    /// </summary>
    /// <param name="tileIndex">Tile index (0 to TotalTiles-1)</param>
    /// <returns>Result indicating if it was safe or a mine</returns>
    public async Task<RevealResult> RevealTileAsync(int tileIndex)
    {
        if (tileIndex < 0 || tileIndex >= TotalTiles)
        {
            return new RevealResult(false, false, 0, 0, $"Tile index must be 0-{TotalTiles - 1}");
        }

        var result = await _client.PerformActionAsync("Reveal", new { TileIndex = tileIndex });
        
        if (!result.Success)
        {
            return new RevealResult(false, false, 0, 0, result.Error);
        }

        var state = ParseState(result.NewState);
        var isMine = state?.Status == LuckyMineStatus.HitMine;
        var winnings = state?.CurrentWinnings ?? 0;

        return new RevealResult(true, isMine, winnings, state?.NextTileWinnings ?? 0, null);
    }

    /// <summary>
    /// 💰 Cash out your current winnings
    /// </summary>
    public async Task<CashOutResult> CashOutAsync()
    {
        var currentWinnings = CurrentWinnings;
        var result = await _client.PerformActionAsync("CashOut");
        
        if (!result.Success)
        {
            return new CashOutResult(false, 0, result.Error);
        }

        return new CashOutResult(true, currentWinnings, null);
    }

    /// <summary>
    /// ❓ Check if a tile has been revealed
    /// </summary>
    public bool IsTileRevealed(int index)
    {
        if (_lastState == null) return false;
        return _lastState.IsRevealed(index);
    }

    /// <summary>
    /// 💣 Check if a revealed tile was a mine (only valid after game ends)
    /// </summary>
    public bool IsTileMine(int index)
    {
        if (_lastState == null || Status == LuckyMineStatus.Active) return false;
        return _lastState.IsMine(index);
    }

    /// <summary>
    /// 📊 Get all revealed tile indices
    /// </summary>
    public int[] GetRevealedTiles()
    {
        if (_lastState == null) return Array.Empty<int>();

        var revealed = new List<int>();
        for (var i = 0; i < TotalTiles; i++)
        {
            if (_lastState.IsRevealed(i))
            {
                revealed.Add(i);
            }
        }
        return revealed.ToArray();
    }

    /// <summary>
    /// 📊 Get all unrevealed tile indices
    /// </summary>
    public int[] GetUnrevealedTiles()
    {
        if (_lastState == null)
        {
            var all = new int[TotalTiles];
            for (var i = 0; i < TotalTiles; i++) all[i] = i;
            return all;
        }

        var unrevealed = new List<int>();
        for (var i = 0; i < TotalTiles; i++)
        {
            if (!_lastState.IsRevealed(i))
            {
                unrevealed.Add(i);
            }
        }
        return unrevealed.ToArray();
    }

    /// <summary>
    /// 🎲 Calculate win probability for next reveal
    /// </summary>
    public double GetNextRevealWinProbability()
    {
        if (_lastState == null || !IsActive) return 0;

        var remaining = TotalTiles - RevealedCount;
        var remainingMines = TotalMines;

        if (remaining <= 0) return 0;
        return (double)(remaining - remainingMines) / remaining;
    }

    /// <summary>
    /// 💰 Calculate potential multiplier if you reveal one more tile
    /// </summary>
    public double GetCurrentMultiplier()
    {
        if (_lastState == null || _lastState.EntryCost == 0) return 1.0;
        return (double)CurrentWinnings / _lastState.EntryCost;
    }

    private void HandleGameState(GameState state)
    {
        if (state.GameType != "LuckyMine") return;

        var mineState = ParseState(state.GameData);
        if (mineState == null) return;

        var wasActive = _lastState?.Status == LuckyMineStatus.Active;
        _lastState = mineState;

        OnStateUpdated?.Invoke(mineState);

        if (wasActive == false && mineState.Status == LuckyMineStatus.Active)
        {
            OnGameStarted?.Invoke(mineState.TotalTiles, mineState.TotalMines);
        }
    }

    private void HandleGameEvent(GameEvent evt)
    {
        switch (evt.EventName)
        {
            case "TileRevealed":
                if (evt.Data is JsonElement revealData)
                {
                    var index = revealData.GetProperty("index").GetInt32();
                    var isMine = revealData.GetProperty("isMine").GetBoolean();
                    OnTileRevealed?.Invoke(index, isMine);

                    if (isMine)
                    {
                        OnMineHit?.Invoke();
                    }
                }
                break;

            case "CashedOut":
                if (evt.Data is JsonElement cashoutData)
                {
                    var amount = cashoutData.GetProperty("amount").GetInt64();
                    OnCashedOut?.Invoke(amount);
                }
                break;
        }
    }

    private static LuckyMineState? ParseState(object? data)
    {
        if (data == null) return null;

        try
        {
            if (data is JsonElement element)
            {
                return JsonSerializer.Deserialize<LuckyMineState>(element.GetRawText());
            }
            var json = JsonSerializer.Serialize(data);
            return JsonSerializer.Deserialize<LuckyMineState>(json);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Game status</summary>
public enum LuckyMineStatus : byte
{
    Active = 0,
    HitMine = 1,
    CashedOut = 2
}

/// <summary>LuckyMine game state</summary>
public sealed class LuckyMineState
{
    public ulong MineMask0 { get; set; }
    public ulong MineMask1 { get; set; }
    public ulong RevealedMask0 { get; set; }
    public ulong RevealedMask1 { get; set; }
    public int RevealedSafeCount { get; set; }
    public byte TotalMines { get; set; }
    public byte TotalTiles { get; set; }
    public LuckyMineStatus Status { get; set; }
    public int EntryCost { get; set; }
    public float RewardSlope { get; set; }
    public long CurrentWinnings { get; set; }
    public long NextTileWinnings { get; set; }

    public bool IsMine(int index)
    {
        if (index < 0 || index >= 128) return false;
        return index < 64
            ? (MineMask0 & (1UL << index)) != 0
            : (MineMask1 & (1UL << (index - 64))) != 0;
    }

    public bool IsRevealed(int index)
    {
        if (index < 0 || index >= 128) return false;
        return index < 64
            ? (RevealedMask0 & (1UL << index)) != 0
            : (RevealedMask1 & (1UL << (index - 64))) != 0;
    }
}

/// <summary>Result of revealing a tile</summary>
public sealed record RevealResult(bool Success, bool IsMine, long CurrentWinnings, long NextTileWinnings, string? Error);

/// <summary>Result of cashing out</summary>
public sealed record CashOutResult(bool Success, long Amount, string? Error);
