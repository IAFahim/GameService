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

        var totalTiles = meta.Config.TryGetValue("TotalTiles", out var tStr) && int.TryParse(tStr, out var t) ? t : 100;
        var mineCount = meta.Config.TryGetValue("TotalMines", out var mStr) && int.TryParse(mStr, out var m) ? m : 20;

        totalTiles = Math.Clamp(totalTiles, 10, 128);
        mineCount = Math.Clamp(mineCount, 1, totalTiles - 1);

        var state = new LuckyMineState
        {
            TotalTiles = (byte)totalTiles,
            TotalMines = (byte)mineCount,
            EntryCost = (int)meta.EntryFee,
            RewardSlope = 0.5f,
            Status = (byte)LuckyMineStatus.Active
        };

        PopulateMines(ref state, totalTiles, mineCount);

        await _repository.SaveAsync(roomId, state, meta);
        logger.LogInformation("Created LuckyMine room {RoomId} (Mines: {Mines}/{Tiles})", roomId, mineCount, totalTiles);

        return roomId;
    }

    public async Task DeleteRoomAsync(string roomId) => await _repository.DeleteAsync(roomId);

    public async Task<JoinRoomResult> JoinRoomAsync(string roomId, string userId)
    {
        var ctx = await _repository.LoadAsync(roomId);
        if (ctx == null) return JoinRoomResult.Error("Room not found");

        if (ctx.Meta.PlayerSeats.TryGetValue(userId, out var seat)) return JoinRoomResult.Ok(seat);
        if (ctx.Meta.PlayerSeats.Count >= ctx.Meta.MaxPlayers) return JoinRoomResult.Error("Room full");

        var newSeat = ctx.Meta.PlayerSeats.Count;
        var newSeats = new Dictionary<string, int>(ctx.Meta.PlayerSeats) { [userId] = newSeat };

        await _repository.SaveAsync(roomId, ctx.State, ctx.Meta with { PlayerSeats = newSeats });
        return JoinRoomResult.Ok(newSeat);
    }

    public async Task LeaveRoomAsync(string roomId, string userId)
    {
        var ctx = await _repository.LoadAsync(roomId);
        if (ctx != null && ctx.Meta.PlayerSeats.ContainsKey(userId))
        {
            var newSeats = new Dictionary<string, int>(ctx.Meta.PlayerSeats);
            newSeats.Remove(userId);
            await _repository.SaveAsync(roomId, ctx.State, ctx.Meta with { PlayerSeats = newSeats });
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