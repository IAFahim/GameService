using System.Security.Cryptography;
using GameService.GameCore;
using Microsoft.Extensions.Logging;

namespace GameService.LuckyMine;


public sealed class LuckyMineRoomService(
    IGameRepositoryFactory repoFactory,
    ILogger<LuckyMineRoomService> logger) : IGameRoomService
{
    private readonly IGameRepository<LuckyMineState> _repository 
        = repoFactory.Create<LuckyMineState>("LuckyMine");

    public string GameType => "LuckyMine";

    public async Task<string> CreateRoomAsync(GameRoomMeta meta)
    {
        var roomId = GenerateShortId();

        int totalTiles = 100;
        int mineCount = 20;

        if (meta.Config.TryGetValue("TotalTiles", out var tilesStr) && int.TryParse(tilesStr, out var t)) totalTiles = t;
        if (meta.Config.TryGetValue("TotalMines", out var minesStr) && int.TryParse(minesStr, out var m)) mineCount = m;

        if (totalTiles > 128) totalTiles = 128;
        if (mineCount >= totalTiles) mineCount = totalTiles - 1;

        var state = new LuckyMineState
        {
            TotalTiles = (byte)totalTiles,
            TotalMines = (byte)mineCount,
            JackpotCounter = (int)(meta.EntryFee * 100),
            EntryCost = (int)meta.EntryFee,
            RewardSlope = 0.5f,
            Status = (byte)LuckyMineStatus.Active
        };

        PopulateMines(ref state, totalTiles, mineCount);

        await _repository.SaveAsync(roomId, state, meta);
        logger.LogInformation("Created LuckyMine room {RoomId} (Mines: {Mines})", roomId, mineCount);

        return roomId;
    }

    private string GenerateShortId()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return string.Create(5, chars, (span, charset) => 
        {
            for(int i=0; i<span.Length; i++) span[i] = charset[RandomNumberGenerator.GetInt32(charset.Length)];
        });
    }

    /// <summary>
    /// Clever algorithm for populating N bits in a range.
    /// Uses Fisher-Yates on a virtual index array.
    /// </summary>
    private void PopulateMines(ref LuckyMineState state, int totalTiles, int mineCount)
    {
        Span<int> indices = stackalloc int[totalTiles];
        for (int i = 0; i < totalTiles; i++) indices[i] = i;

        for (int i = 0; i < mineCount; i++)
        {
            int j = RandomNumberGenerator.GetInt32(i, totalTiles);
            (indices[i], indices[j]) = (indices[j], indices[i]);

            int mineIdx = indices[i];
            if (mineIdx < 64) state.MineMask0 |= (1UL << mineIdx);
            else state.MineMask1 |= (1UL << (mineIdx - 64));
        }
    }

    public async Task DeleteRoomAsync(string roomId) => await _repository.DeleteAsync(roomId);

    public async Task<JoinRoomResult> JoinRoomAsync(string roomId, string userId)
    {
        var ctx = await _repository.LoadAsync(roomId);
        if (ctx == null) return JoinRoomResult.Error("Room not found");

        if (ctx.Meta.PlayerSeats.TryGetValue(userId, out var seat)) return JoinRoomResult.Ok(seat);
        if (ctx.Meta.PlayerSeats.Count >= ctx.Meta.MaxPlayers) return JoinRoomResult.Error("Room full");

        int newSeat = ctx.Meta.PlayerSeats.Count;
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

    public async Task<GameRoomMeta?> GetRoomMetaAsync(string roomId)
    {
        var ctx = await _repository.LoadAsync(roomId);
        return ctx?.Meta;
    }
}