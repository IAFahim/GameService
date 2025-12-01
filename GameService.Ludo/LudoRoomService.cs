using System.Security.Cryptography;
using GameService.GameCore;
using Microsoft.Extensions.Logging;

namespace GameService.Ludo;

/// <summary>
/// Ludo room service for room lifecycle management.
/// Uses the generic repository pattern.
/// </summary>
public sealed class LudoRoomService : IGameRoomService
{
    private readonly IGameRepository<LudoState> _repository;
    private readonly IRoomRegistry _roomRegistry;
    private readonly ILogger<LudoRoomService> _logger;

    public string GameType => "Ludo";

    public LudoRoomService(
        IGameRepositoryFactory repositoryFactory,
        IRoomRegistry roomRegistry,
        ILogger<LudoRoomService> logger)
    {
        _repository = repositoryFactory.Create<LudoState>(GameType);
        _roomRegistry = roomRegistry;
        _logger = logger;
    }

    public async Task<string> CreateRoomAsync(GameRoomMeta meta)
    {
        var roomId = GenerateShortId();
        
        var engine = new LudoEngine(new ServerDiceRoller());
        engine.InitNewGame(meta.MaxPlayers);
        
        if (engine.State.Winner == 0) engine.State.Winner = 255;

        await _repository.SaveAsync(roomId, engine.State, meta);
        _logger.LogInformation("Created Ludo room {RoomId}", roomId);
        return roomId;
    }

    private string GenerateShortId()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return string.Create(6, chars, (span, charset) => {
            for(int i=0; i<span.Length; i++) span[i] = charset[RandomNumberGenerator.GetInt32(charset.Length)];
        });
    }

    public async Task DeleteRoomAsync(string roomId)
    {
        await _repository.DeleteAsync(roomId);
        _logger.LogInformation("Deleted Ludo room {RoomId}", roomId);
    }

    public async Task<JoinRoomResult> JoinRoomAsync(string roomId, string userId)
    {
        var ctx = await _repository.LoadAsync(roomId);
        if (ctx == null)
            return JoinRoomResult.Error("Room not found");

        if (ctx.Meta.PlayerSeats.TryGetValue(userId, out var existingSeat))
            return JoinRoomResult.Ok(existingSeat);

        if (ctx.Meta.PlayerSeats.Count >= ctx.Meta.MaxPlayers)
            return JoinRoomResult.Error("Room is full");

        var takenSeats = ctx.Meta.PlayerSeats.Values.ToHashSet();
        var seatIndex = -1;

        for (int i = 0; i < 4; i++)
        {
            bool isSeatActive = (ctx.State.ActiveSeats & (1 << i)) != 0;
            
            if (isSeatActive && !takenSeats.Contains(i))
            {
                seatIndex = i;
                break;
            }
        }

        if (seatIndex == -1)
            return JoinRoomResult.Error("No available seats");

        var newSeats = new Dictionary<string, int>(ctx.Meta.PlayerSeats)
        {
            [userId] = seatIndex
        };

        var newMeta = ctx.Meta with { PlayerSeats = newSeats };
        await _repository.SaveAsync(roomId, ctx.State, newMeta);

        _logger.LogInformation("Player {UserId} joined room {RoomId} at seat {Seat}", userId, roomId, seatIndex);
        
        return JoinRoomResult.Ok(seatIndex);
    }

    public async Task LeaveRoomAsync(string roomId, string userId)
    {
        var ctx = await _repository.LoadAsync(roomId);
        if (ctx == null) return;

        if (!ctx.Meta.PlayerSeats.ContainsKey(userId)) return;

        var newSeats = new Dictionary<string, int>(ctx.Meta.PlayerSeats);
        newSeats.Remove(userId);

        var newMeta = ctx.Meta with { PlayerSeats = newSeats };
        await _repository.SaveAsync(roomId, ctx.State, newMeta);

        _logger.LogInformation("Player {UserId} left room {RoomId}", userId, roomId);
    }

    public async Task<GameRoomMeta?> GetRoomMetaAsync(string roomId)
    {
        var ctx = await _repository.LoadAsync(roomId);
        return ctx?.Meta;
    }
}