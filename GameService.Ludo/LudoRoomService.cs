using System.Security.Cryptography;
using GameService.GameCore;
using Microsoft.Extensions.Logging;

namespace GameService.Ludo;

public sealed class LudoRoomService : IGameRoomService
{
    private readonly ILogger<LudoRoomService> _logger;
    private readonly IGameRepository<LudoState> _repository;

    public LudoRoomService(IGameRepositoryFactory factory, ILogger<LudoRoomService> logger)
    {
        _repository = factory.Create<LudoState>("Ludo");
        _logger = logger;
    }

    public string GameType => "Ludo";

    public async Task<string> CreateRoomAsync(GameRoomMeta meta)
    {
        var roomId = GenerateId();
        var engine = new LudoEngine(new ServerDiceRoller());
        engine.InitNewGame(meta.MaxPlayers);
        
        await _repository.SaveAsync(roomId, engine.State, meta);
        _logger.LogInformation("Created Ludo room {RoomId}", roomId);
        return roomId;
    }

    public async Task<JoinRoomResult> JoinRoomAsync(string roomId, string userId)
    {
        var ctx = await _repository.LoadAsync(roomId);
        if (ctx == null) return JoinRoomResult.Error("Room not found");

        if (ctx.Meta.PlayerSeats.TryGetValue(userId, out var seat)) return JoinRoomResult.Ok(seat);
        if (ctx.Meta.PlayerSeats.Count >= ctx.Meta.MaxPlayers) return JoinRoomResult.Error("Room full");

        var taken = ctx.Meta.PlayerSeats.Values.ToHashSet();
        int newSeat = -1;
        
        for (int i = 0; i < 4; i++)
        {
            if ((ctx.State.ActiveSeats & (1 << i)) != 0 && !taken.Contains(i))
            {
                newSeat = i; break;
            }
        }

        if (newSeat == -1) return JoinRoomResult.Error("No seats available");

        var newSeats = new Dictionary<string, int>(ctx.Meta.PlayerSeats) { [userId] = newSeat };
        await _repository.SaveAsync(roomId, ctx.State, ctx.Meta with { PlayerSeats = newSeats });

        return JoinRoomResult.Ok(newSeat);
    }

    public async Task DeleteRoomAsync(string roomId) => await _repository.DeleteAsync(roomId);
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
}