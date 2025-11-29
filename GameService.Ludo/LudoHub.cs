using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace GameService.Ludo;

[Authorize]
public class LudoHub(LudoRoomService roomService) : Hub
{
    private string UserId => Context.User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    public async Task<string> CreateGame()
    {
        var roomId = await roomService.CreateRoomAsync(UserId);
        await Groups.AddToGroupAsync(Context.ConnectionId, roomId);

        await Clients.Caller.SendAsync("RoomCreated", roomId);
        return roomId;
    }

    public async Task<bool> JoinGame(string roomId)
    {
        if (await roomService.JoinRoomAsync(roomId, UserId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, roomId);

            var ctx = await roomService.LoadGameAsync(roomId);
            if(ctx != null)
                await Clients.Caller.SendAsync("GameState", SerializeState(ctx.State));
                
            await Clients.Group(roomId).SendAsync("PlayerJoined", UserId);
            return true;
        }
        return false;
    }

    public async Task RollDice(string roomId)
    {
        var ctx = await roomService.LoadGameAsync(roomId);
        if (ctx == null) { await Clients.Caller.SendAsync("Error", "Room not found"); return; }

        if (!ctx.Meta.PlayerSeats.TryGetValue(UserId, out int mySeat)) { await Clients.Caller.SendAsync("Error", "You are not in this room"); return; }

        var engine = new LudoEngine(ctx.State, new ServerDiceRoller());
    
        // Check turn
        if (engine.State.CurrentPlayer != mySeat) 
        {
            await Clients.Caller.SendAsync("Error", $"Not your turn! Waiting for Seat {engine.State.CurrentPlayer}");
            return;
        }

        if (engine.TryRollDice(out var result))
        {
            var newCtx = ctx with { State = engine.State };
            await roomService.SaveGameAsync(newCtx);
            await Clients.Group(roomId).SendAsync("RollResult", result.DiceValue);
        
            // Send state update if turn passed (e.g. rolled a 1, 2, 3...)
            if (result.Status == LudoStatus.TurnPassed || result.Status == LudoStatus.ForfeitTurn)
                await Clients.Group(roomId).SendAsync("GameState", SerializeState(engine.State));
        }
        else
        {
            // FIX: Tell client WHY it failed
            await Clients.Caller.SendAsync("Error", $"Roll Rejected: {result.Status} (Did you already roll?)");
        }
    }

    public async Task MoveToken(string roomId, int tokenIndex)
    {
        var ctx = await roomService.LoadGameAsync(roomId);
        if (ctx == null) return;

        if (!ctx.Meta.PlayerSeats.TryGetValue(UserId, out int mySeat)) return;
        
        var engine = new LudoEngine(ctx.State, new ServerDiceRoller());
        
        if (engine.State.CurrentPlayer != mySeat) return;

        if (engine.TryMoveToken(tokenIndex, out var result))
        {
            var newCtx = ctx with { State = engine.State };
            await roomService.SaveGameAsync(newCtx);

            await Clients.Group(roomId).SendAsync("GameState", SerializeState(engine.State));
            
            if ((result.Status & LudoStatus.GameWon) != 0)
            {
                await Clients.Group(roomId).SendAsync("GameWon", UserId);
            }
        }
        else
        {
            await Clients.Caller.SendAsync("Error", $"Move Rejected: {result.Status}");
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await base.OnDisconnectedAsync(exception);
    }
    

    private byte[] SerializeState(LudoState state)
    {
        unsafe 
        {
            var bytes = new byte[sizeof(LudoState)];
            fixed (byte* b = bytes) { *(LudoState*)b = state; }
            return bytes;
        }
    }
}