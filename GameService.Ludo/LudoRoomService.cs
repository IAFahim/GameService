using System.Text.Json;
using GameService.GameCore;
using StackExchange.Redis;

namespace GameService.Ludo;

public class LudoRoomService(ILudoRepository repository) : IGameRoomService
{
    public async Task<string> CreateRoomAsync(string? hostUserId, int playerCount = 4)
    {
        string roomId = Guid.NewGuid().ToString("N")[..8];

        var engine = new LudoEngine(new ServerDiceRoller());
        engine.InitNewGame(playerCount);

        // DEBUG: Ensure Winner is 255 (Not 0)
        if (engine.State.Winner == 0) engine.State.Winner = 255;

        var meta = new LudoRoomMeta { 
            PlayerSeats = new(),
            IsPublic = true,
            MaxPlayers = playerCount
        };

        if (!string.IsNullOrEmpty(hostUserId))
        {
            meta.PlayerSeats.Add(hostUserId, 0);
        }

        // IMPORTANT: Pass engine.State (which is a Value Type copy) explicitly
        var context = new LudoContext(roomId, engine.State, meta);
    
        await repository.SaveGameAsync(context);
        await repository.AddActiveGameAsync(roomId);
    
        return roomId;
    }

    public async Task<List<GameRoomDto>> GetActiveGamesAsync()
    {
        var games = await repository.GetActiveGamesAsync();
        return games.Select(g => new GameRoomDto(
            g.RoomId, 
            "Ludo", 
            g.Meta.PlayerSeats.Count, 
            g.Meta.IsPublic, 
            g.Meta.PlayerSeats
        )).ToList();
    }

    public async Task<object?> GetGameStateAsync(string roomId)
    {
        var ctx = await repository.LoadGameAsync(roomId);
        if (ctx == null) return null;

        // FIX: Manually map the struct fields to an anonymous object
        // so System.Text.Json can read them.
        var s = ctx.State;
    
        // Convert token buffer to array
        var tokenArray = new byte[16];
        for(int i=0; i<16; i++) tokenArray[i] = s.Tokens[i];

        return new 
        {
            ctx.RoomId,
            ctx.Meta,
            State = new 
            {
                CurrentPlayer = s.CurrentPlayer,
                LastDiceRoll = s.LastDiceRoll,
                TurnId = s.TurnId,
                Winner = s.Winner == 255 ? -1 : (int)s.Winner,
                ActiveSeatsBinary = Convert.ToString(s.ActiveSeats, 2).PadLeft(4, '0'),
                Tokens = tokenArray
            }
        };
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
    public int MaxPlayers { get; set; } = 4;
}

public record LudoContext(string RoomId, LudoState State, LudoRoomMeta Meta);