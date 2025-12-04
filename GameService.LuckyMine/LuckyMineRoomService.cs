using System.Security.Cryptography;
using GameService.GameCore;
using Microsoft.Extensions.Logging;

namespace GameService.LuckyMine;

public sealed class LuckyMineRoomService(
    IGameRepositoryFactory repoFactory,
    ILogger<LuckyMineRoomService> logger) : IGameRoomService
{
    private readonly IGameRepository<LuckyMineState> _repository = repoFactory.Create<LuckyMineState>("LuckyMine");

    public string GameType => "LuckyMine";

    public async Task<string> CreateRoomAsync(GameRoomMeta meta)
    {
        var roomId = GenerateId();

        var totalTiles = meta.Config.TryGetValue("TotalTiles", out var tStr) && int.TryParse(tStr, out var t) ? t : 25;
        var mineCount = meta.Config.TryGetValue("TotalMines", out var mStr) && int.TryParse(mStr, out var m) ? m : 5;

        totalTiles = Math.Clamp(totalTiles, 10, 128);
        mineCount = Math.Clamp(mineCount, 1, totalTiles - 1);

        // Single player game - MaxPlayers is always 1
        var singlePlayerMeta = meta with { MaxPlayers = 1 };

        var state = new LuckyMineState
        {
            TotalTiles = (byte)totalTiles,
            TotalMines = (byte)mineCount,
            EntryCost = (int)meta.EntryFee,
            RewardSlope = 0.5f,
            Status = (byte)LuckyMineStatus.Active,
            RevealedSafeCount = 0,
            CurrentWinnings = 0
        };

        PopulateMines(ref state, totalTiles, mineCount);

        await _repository.SaveAsync(roomId, state, singlePlayerMeta);
        logger.LogInformation("Created LuckyMine room {RoomId} (Mines: {Mines}/{Tiles})", roomId, mineCount, totalTiles);

        return roomId;
    }

    public async Task DeleteRoomAsync(string roomId) => await _repository.DeleteAsync(roomId);

    public async Task<JoinRoomResult> JoinRoomAsync(string roomId, string userId)
    {
        // Acquire lock to prevent race conditions
        if (!await _repository.TryAcquireLockAsync(roomId, TimeSpan.FromSeconds(5)))
            return JoinRoomResult.Error("Room is busy. Please try again.");

        try
        {
            var ctx = await _repository.LoadAsync(roomId);
            if (ctx == null) return JoinRoomResult.Error("Room not found");

            // Already in the game
            if (ctx.Meta.PlayerSeats.TryGetValue(userId, out var seat)) return JoinRoomResult.Ok(seat);
            
            // Single player - only one player allowed
            if (ctx.Meta.PlayerSeats.Count >= 1) return JoinRoomResult.Error("Room already has a player");

            var newSeats = new Dictionary<string, int>(ctx.Meta.PlayerSeats) { [userId] = 0 };

            await _repository.SaveAsync(roomId, ctx.State, ctx.Meta with { PlayerSeats = newSeats });
            return JoinRoomResult.Ok(0);
        }
        finally
        {
            await _repository.ReleaseLockAsync(roomId);
        }
    }

    public async Task LeaveRoomAsync(string roomId, string userId)
    {
        if (!await _repository.TryAcquireLockAsync(roomId, TimeSpan.FromSeconds(5)))
            return; // Best effort
        
        try
        {
            var ctx = await _repository.LoadAsync(roomId);
            if (ctx != null && ctx.Meta.PlayerSeats.ContainsKey(userId))
            {
                var newSeats = new Dictionary<string, int>(ctx.Meta.PlayerSeats);
                newSeats.Remove(userId);
                await _repository.SaveAsync(roomId, ctx.State, ctx.Meta with { PlayerSeats = newSeats });
            }
        }
        finally
        {
            await _repository.ReleaseLockAsync(roomId);
        }
    }

    public async Task<GameRoomMeta?> GetRoomMetaAsync(string roomId) => (await _repository.LoadAsync(roomId))?.Meta;

    private string GenerateId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(3));

    private void PopulateMines(ref LuckyMineState state, int totalTiles, int mineCount)
    {
        Span<int> indices = stackalloc int[totalTiles];
        for (var i = 0; i < totalTiles; i++) indices[i] = i;

        for (var i = 0; i < mineCount; i++)
        {
            var j = Random.Shared.Next(i, totalTiles);
            (indices[i], indices[j]) = (indices[j], indices[i]);
            var mineIdx = indices[i];
            if (mineIdx < 64) state.MineMask0 |= 1UL << mineIdx;
            else state.MineMask1 |= 1UL << (mineIdx - 64);
        }
    }
}