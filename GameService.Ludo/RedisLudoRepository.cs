using System.Text.Json;
using StackExchange.Redis;

namespace GameService.Ludo;

public class RedisLudoRepository(IConnectionMultiplexer redis) : ILudoRepository
{
    private readonly IDatabase _db = redis.GetDatabase();
    
    private string GetMetaKey(string roomId) => $"ludo:{roomId}:meta";
    private string GetStateKey(string roomId) => $"ludo:{roomId}:state";
    private const string ActiveRoomsKey = "ludo:active_rooms";

    public async Task SaveGameAsync(LudoContext context)
    {
        byte[] stateBytes = SerializeState(context.State);
        await _db.StringSetAsync(GetStateKey(context.RoomId), stateBytes);

        await _db.StringSetAsync(GetMetaKey(context.RoomId), JsonSerializer.Serialize(context.Meta, LudoJsonContext.Default.LudoRoomMeta));
    }

    public async Task<LudoContext?> LoadGameAsync(string roomId)
    {
        var stateBytes = await _db.StringGetAsync(GetStateKey(roomId));
        var metaJson = await _db.StringGetAsync(GetMetaKey(roomId));
        
        if (stateBytes.IsNullOrEmpty || metaJson.IsNullOrEmpty) return null;

        LudoState state = DeserializeState((byte[])stateBytes!);
        var meta = JsonSerializer.Deserialize((string)metaJson!, LudoJsonContext.Default.LudoRoomMeta);
        
        return new LudoContext(roomId, state, meta!);
    }

    public async Task<List<LudoContext>> GetActiveGamesAsync()
    {
        var roomIds = await _db.SetMembersAsync(ActiveRoomsKey);
        var games = new List<LudoContext>();
        
        foreach (var id in roomIds)
        {
            var ctx = await LoadGameAsync(id.ToString());
            if (ctx != null) games.Add(ctx);
            else await _db.SetRemoveAsync(ActiveRoomsKey, id);
        }
        
        return games;
    }

    public async Task DeleteGameAsync(string roomId)
    {
        await _db.KeyDeleteAsync([GetMetaKey(roomId), GetStateKey(roomId)]);
        await _db.SetRemoveAsync(ActiveRoomsKey, roomId);
    }

    public async Task<bool> TryJoinRoomAsync(string roomId, string userId)
    {
        var metaKey = GetMetaKey(roomId);

        // This script handles:
        // 1. Checking if game exists
        // 2. Checking if user is already in
        // 3. Checking max players
        // 4. Finding the first empty seat index (0, 1, 2, 3...)
        const string script = @"
            local metaJson = redis.call('GET', KEYS[1])
            if not metaJson then return 0 end
            
            local meta = cjson.decode(metaJson)
            
            -- If user is already seated, return success (1)
            if meta.PlayerSeats[ARGV[1]] then return 1 end
            
            -- Count occupied seats
            local count = 0
            for _ in pairs(meta.PlayerSeats) do count = count + 1 end
            
            -- Check MaxPlayers (default to 4 if missing)
            local max = meta.MaxPlayers or 4
            if count >= max then return 0 end
            
            -- Build a map of taken seats
            local takenSeats = {}
            for _, seat in pairs(meta.PlayerSeats) do takenSeats[seat] = true end
            
            -- Find first available seat index
            local assignedSeat = -1
            for i = 0, (max - 1) do
                if not takenSeats[i] then
                    assignedSeat = i
                    break
                end
            end
            
            if assignedSeat == -1 then return 0 end

            -- Assign seat
            meta.PlayerSeats[ARGV[1]] = assignedSeat
            
            -- Save back
            redis.call('SET', KEYS[1], cjson.encode(meta))
            return 1
        ";

        // FIX: Use explicit Keys and Values instead of LuaScript.Prepare with object mapping
        var keys = new RedisKey[] { metaKey };
        var values = new RedisValue[] { userId };

        try 
        {
            var result = await _db.ScriptEvaluateAsync(script, keys, values);
            return (int)result == 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Redis Error] TryJoinRoomAsync: {ex.Message}");
            return false;
        }
    }

    public async Task AddActiveGameAsync(string roomId)
    {
        await _db.SetAddAsync(ActiveRoomsKey, roomId);
    }

    private static byte[] SerializeState(LudoState state)
    {
        var bytes = new byte[28];
        System.Runtime.InteropServices.MemoryMarshal.Write(bytes, in state);
        return bytes;
    }
    
    private static LudoState DeserializeState(byte[] bytes)
    {
        return System.Runtime.InteropServices.MemoryMarshal.Read<LudoState>(bytes);
    }
}