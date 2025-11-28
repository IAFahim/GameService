using System.Text.Json;
using StackExchange.Redis;

namespace GameService.Ludo;

public class LudoRoomService(ILudoRepository repository)
{
    public async Task<string?> CreateRoomAsync(string hostUserId)
    {
        string roomId = Guid.NewGuid().ToString("N")[..8];

        var engine = new LudoEngine(new ServerDiceRoller());
        engine.InitNewGame(2);

        var meta = new LudoRoomMeta { 
            PlayerSeats = new() { { hostUserId, 0 } },
            IsPublic = true 
        };

        var context = new LudoContext(roomId, engine, meta);
        await repository.SaveGameAsync(context);
        await repository.AddActiveGameAsync(roomId);
        
        return roomId;
    }

    public async Task<List<LudoContext>> GetActiveGamesAsync()
    {
        return await repository.GetActiveGamesAsync();
    }

    public async Task DeleteRoomAsync(string roomId)
    {
        await repository.DeleteGameAsync(roomId);
    }
    
    public async Task<bool> JoinRoomAsync(string roomId, string userId)
    {
        return await repository.TryJoinRoomAsync(roomId, userId);
    }
    
    public async Task<LudoContext?> LoadGameAsync(string roomId)
    {
        return await repository.LoadGameAsync(roomId);
    }
    
    public async Task SaveGameAsync(LudoContext ctx)
    {
        await repository.SaveGameAsync(ctx);
    }
}

public record LudoRoomMeta
{
    public Dictionary<string, int> PlayerSeats { get; set; } = new();
    public bool IsPublic { get; set; }
    public string GameType { get; set; } = "Ludo";
}

public record LudoContext(string RoomId, LudoEngine Engine, LudoRoomMeta Meta);