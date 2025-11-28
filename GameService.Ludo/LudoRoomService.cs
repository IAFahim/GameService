using System.Text.Json;
using StackExchange.Redis;

namespace GameService.Ludo;

public class LudoRoomService(IConnectionMultiplexer redis)
{
    private readonly IDatabase _db = redis.GetDatabase();
    
    private string GetMetaKey(string roomId) => $"ludo:{roomId}:meta";
    private string GetStateKey(string roomId) => $"ludo:{roomId}:state";
    private const string ActiveRoomsKey = "ludo:active_rooms";
    
    public async Task<string?> CreateRoomAsync(string hostUserId)
    {
        string roomId = Guid.NewGuid().ToString("N")[..8];

        var engine = new LudoEngine(new ServerDiceRoller());
        engine.InitNewGame(2);

        byte[] stateBytes = SerializeState(engine.State);
        await _db.StringSetAsync(GetStateKey(roomId), stateBytes);

        var meta = new LudoRoomMeta { 
            PlayerSeats = new() { { hostUserId, 0 } },
            IsPublic = true 
        };
        await _db.StringSetAsync(GetMetaKey(roomId), JsonSerializer.Serialize(meta));
        await _db.SetAddAsync(ActiveRoomsKey, roomId);
        
        return roomId;
    }

    public async Task<List<LudoContext>> GetActiveGamesAsync()
    {
        var roomIds = await _db.SetMembersAsync(ActiveRoomsKey);
        var games = new List<LudoContext>();
        
        foreach (var id in roomIds)
        {
            var ctx = await LoadGameAsync(id.ToString());
            if (ctx != null) games.Add(ctx);
            else await _db.SetRemoveAsync(ActiveRoomsKey, id); // Cleanup stale
        }
        
        return games;
    }

    public async Task DeleteRoomAsync(string roomId)
    {
        await _db.KeyDeleteAsync([GetMetaKey(roomId), GetStateKey(roomId)]);
        await _db.SetRemoveAsync(ActiveRoomsKey, roomId);
    }
    
    public async Task<bool> JoinRoomAsync(string roomId, string userId)
    {
        var metaKey = GetMetaKey(roomId);
        
        // Lua script to atomically check capacity and join
        // Keys: { metaKey }
        // Args: { userId }
        const string script = @"
            var metaJson = redis.call('GET', KEYS[1])
            if not metaJson then return 0 end
            
            var meta = cjson.decode(metaJson)
            
            if meta.PlayerSeats[ARGV[1]] then return 1 end
            
            -- Count keys in PlayerSeats
            var count = 0
            for _ in pairs(meta.PlayerSeats) do count = count + 1 end
            
            if count >= 2 then return 0 end
            
            -- Assign seat 2 (for 2 player game)
            meta.PlayerSeats[ARGV[1]] = 2
            
            redis.call('SET', KEYS[1], cjson.encode(meta))
            return 1
        ";

        var result = await _db.ScriptEvaluateAsync(LuaScript.Prepare(script), new { metaKey = (RedisKey)metaKey, userId });
        return (int)result == 1;
    }
    
    public async Task<LudoContext?> LoadGameAsync(string roomId)
    {
        var stateBytes = await _db.StringGetAsync(GetStateKey(roomId));
        var metaJson = await _db.StringGetAsync(GetMetaKey(roomId));
        
        if (stateBytes.IsNullOrEmpty || metaJson.IsNullOrEmpty) return null;

        LudoState state = DeserializeState((byte[])stateBytes!);
        var meta = JsonSerializer.Deserialize<LudoRoomMeta>((string)metaJson!);
        
        return new LudoContext(roomId, new LudoEngine(state, new ServerDiceRoller()), meta!);
    }
    
    public async Task SaveGameAsync(LudoContext ctx)
    {
        byte[] stateBytes = SerializeState(ctx.Engine.State);
        await _db.StringSetAsync(GetStateKey(ctx.RoomId), stateBytes);
    }

    private static unsafe byte[] SerializeState(LudoState state)
    {
        var bytes = new byte[sizeof(LudoState)];
        fixed (byte* b = bytes) 
        { 
            *(LudoState*)b = state; 
        }
        return bytes;
    }
    
    private static unsafe LudoState DeserializeState(byte[] bytes)
    {
        fixed (byte* ptr = bytes) 
        { 
            return *(LudoState*)ptr; 
        }
    }
}

public record LudoRoomMeta
{
    public Dictionary<string, int> PlayerSeats { get; set; } = new();
    public bool IsPublic { get; set; }
    public string GameType { get; set; } = "Ludo";
}

public record LudoContext(string RoomId, LudoEngine Engine, LudoRoomMeta Meta);