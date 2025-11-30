using System.Text.Json;
using GameService.GameCore;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace GameService.Ludo;

public class LudoRoomService(ILudoRepository repository, IHubContext<LudoHub> hubContext) : IGameRoomService
{
    public string GameType => "Ludo";
    public async Task<string> CreateRoomAsync(string? hostUserId, int playerCount = 4)
    {
        string roomId = Guid.NewGuid().ToString("N")[..8];
        var engine = new LudoEngine(new ServerDiceRoller());
        engine.InitNewGame(playerCount);
        if (engine.State.Winner == 0) engine.State.Winner = 255;
        var meta = new LudoRoomMeta { PlayerSeats = new(), IsPublic = true, MaxPlayers = playerCount };
        if (!string.IsNullOrEmpty(hostUserId)) meta.PlayerSeats.Add(hostUserId, 0);
        var context = new LudoContext(roomId, engine.State, meta);
        await repository.SaveGameAsync(context);
        await repository.AddActiveGameAsync(roomId);
        return roomId;
    }
    
    public async Task DeleteRoomAsync(string roomId) => await repository.DeleteGameAsync(roomId);
    public async Task<bool> JoinRoomAsync(string roomId, string userId) => await repository.TryJoinRoomAsync(roomId, userId);
    public async Task<LudoContext?> LoadGameAsync(string roomId) => await repository.LoadGameAsync(roomId);
    public async Task SaveGameAsync(LudoContext ctx) => await repository.SaveGameAsync(ctx);
    
    public async Task<LudoMoveResult> PerformRollAsync(string roomId, string userId, bool bypassChecks = false)
    {
        var ctx = await repository.LoadGameAsync(roomId);
        if (ctx == null) return new LudoMoveResult(false, "Room not found");

        int seatIndex = -1;

        // If bypass is on (Admin), userId is treated as a generic identifier, 
        // OR we can pass the seat directly. Let's look up seat by userId normally.
        // For Admin impersonation, we will overload this or handle seat lookup externally.
        if (bypassChecks)
        {
            // If bypassing, we assume the CurrentPlayer is who we want to act as
            seatIndex = ctx.State.CurrentPlayer;
        }
        else
        {
            if (!ctx.Meta.PlayerSeats.TryGetValue(userId, out seatIndex)) 
                return new LudoMoveResult(false, "Player not in room");
        }

        var engine = new LudoEngine(ctx.State, new ServerDiceRoller());

        if (engine.State.CurrentPlayer != seatIndex)
            return new LudoMoveResult(false, $"Not your turn. Waiting for Seat {engine.State.CurrentPlayer}");

        if (engine.TryRollDice(out var result))
        {
            var newCtx = ctx with { State = engine.State };
            await repository.SaveGameAsync(newCtx);

            // Broadcast to everyone (Players see the roll)
            await hubContext.Clients.Group(roomId).SendAsync("RollResult", result.DiceValue);
            
            if (result.Status == LudoStatus.TurnPassed || result.Status == LudoStatus.ForfeitTurn)
            {
                await BroadcastStateAsync(roomId, engine.State);
            }

            return new LudoMoveResult(true, "Rolled " + result.DiceValue);
        }

        return new LudoMoveResult(false, $"Roll rejected: {result.Status}");
    }
    
    public async Task<LudoMoveResult> PerformMoveAsync(string roomId, string userId, int tokenIndex, bool bypassChecks = false)
    {
        var ctx = await repository.LoadGameAsync(roomId);
        if (ctx == null) return new LudoMoveResult(false, "Room not found");

        int seatIndex;
        if (bypassChecks)
        {
            seatIndex = ctx.State.CurrentPlayer;
        }
        else
        {
            if (!ctx.Meta.PlayerSeats.TryGetValue(userId, out seatIndex))
                return new LudoMoveResult(false, "Player not in room");
        }

        var engine = new LudoEngine(ctx.State, new ServerDiceRoller());

        if (engine.State.CurrentPlayer != seatIndex)
            return new LudoMoveResult(false, "Not your turn");

        if (engine.TryMoveToken(tokenIndex, out var result))
        {
            var newCtx = ctx with { State = engine.State };
            await repository.SaveGameAsync(newCtx);

            await BroadcastStateAsync(roomId, engine.State);

            if ((result.Status & LudoStatus.GameWon) != 0)
            {
                await hubContext.Clients.Group(roomId).SendAsync("GameWon", userId); // Or Seat Index
            }
            
            return new LudoMoveResult(true, "Token Moved");
        }

        return new LudoMoveResult(false, $"Move rejected: {result.Status}");
    }
    
    private async Task BroadcastStateAsync(string roomId, LudoState state)
    {
        // We broadcast using the helper to ensure consistent binary serialization
        // Note: We need to use the public LudoHub serialization logic, or duplicate it here.
        // For cleanness, we'll implement the byte serialization here locally or make it static.
        byte[] data = LudoStateSerializer.Serialize(state);
        await hubContext.Clients.Group(roomId).SendAsync("GameState", data);
    }

    public async Task<List<GameRoomDto>> GetActiveGamesAsync()
    {
        var games = await repository.GetActiveGamesAsync();
        return games.Select(g => new GameRoomDto(g.RoomId, "Ludo", g.Meta.PlayerSeats.Count, g.Meta.IsPublic, g.Meta.PlayerSeats)).ToList();
    }

    public async Task<object?> GetGameStateAsync(string roomId)
    {
        var ctx = await repository.LoadGameAsync(roomId);
        if (ctx == null) return null;

        var s = ctx.State;
        var engine = new LudoEngine(s, null!); // Dice roller not needed for state query
        
        // Calculate legal moves for the UI
        var legalMoves = engine.GetLegalMoves();

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
                Tokens = tokenArray,
                LegalMoves = legalMoves
            }
        };
    }
}

public record LudoMoveResult(bool Success, string Message);

public record LudoRoomMeta
{
    public Dictionary<string, int> PlayerSeats { get; set; } = new();
    public bool IsPublic { get; set; }
    public string GameType { get; set; } = "Ludo";
    public int MaxPlayers { get; set; } = 4;
}

public static class LudoStateSerializer 
{
    public static byte[] Serialize(LudoState state) 
    {
        unsafe {
            var bytes = new byte[sizeof(LudoState)];
            fixed (byte* b = bytes) { *(LudoState*)b = state; }
            return bytes;
        }
    }
}


public record LudoContext(string RoomId, LudoState State, LudoRoomMeta Meta);