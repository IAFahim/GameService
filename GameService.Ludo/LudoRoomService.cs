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

    public async Task<string> CreateRoomAsync(string? hostUserId, int playerCount = 4)
    {
        var roomId = Guid.NewGuid().ToString("N")[..8];
        
        var engine = new LudoEngine(new ServerDiceRoller());
        engine.InitNewGame(playerCount);
        if (engine.State.Winner == 0) engine.State.Winner = 255;

        var meta = new GameRoomMeta
        {
            PlayerSeats = hostUserId != null ? new Dictionary<string, int> { [hostUserId] = 0 } : new(),
            IsPublic = true,
            GameType = GameType,
            MaxPlayers = playerCount
        };

        await _repository.SaveAsync(roomId, engine.State, meta);
        
        _logger.LogInformation("Created Ludo room {RoomId} with {PlayerCount} players", roomId, playerCount);
        
        return roomId;
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

        // Check if already in room
        if (ctx.Meta.PlayerSeats.TryGetValue(userId, out var existingSeat))
            return JoinRoomResult.Ok(existingSeat);

        // Check if room is full
        if (ctx.Meta.PlayerSeats.Count >= ctx.Meta.MaxPlayers)
            return JoinRoomResult.Error("Room is full");

        // Find available seat
        var takenSeats = ctx.Meta.PlayerSeats.Values.ToHashSet();
        var seatIndex = -1;
        for (int i = 0; i < ctx.Meta.MaxPlayers; i++)
        {
            if (!takenSeats.Contains(i))
            {
                seatIndex = i;
                break;
            }
        }

        if (seatIndex == -1)
            return JoinRoomResult.Error("No available seats");

        // Update metadata with new player
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