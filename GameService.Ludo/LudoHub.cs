using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace GameService.Ludo;

[Authorize]
public class LudoHub(LudoRoomService roomService) : Hub
{
    private string UserId => Context.User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    // GameService.Ludo/LudoHub.cs
    public async Task CreateGame()
    {
        var roomId = await roomService.CreateRoomAsync(UserId);
        await Groups.AddToGroupAsync(Context.ConnectionId, roomId);
    
        // IMPORTANT: Send the ID back!
        await Clients.Caller.SendAsync("RoomCreated", roomId);
    }

    public async Task<bool> JoinGame(string roomId)
    {
        if (await roomService.JoinRoomAsync(roomId, UserId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, roomId);
            
            // Sync initial state
            var ctx = await roomService.LoadGameAsync(roomId);
            if(ctx != null)
                await Clients.Caller.SendAsync("GameState", SerializeState(ctx.Engine.State));
                
            await Clients.Group(roomId).SendAsync("PlayerJoined", UserId);
            return true;
        }
        return false;
    }

    public async Task RollDice(string roomId)
    {
        var ctx = await roomService.LoadGameAsync(roomId);
        if (ctx == null) return;

        // 1. Validate Turn
        if (!ctx.Meta.PlayerSeats.TryGetValue(UserId, out int mySeat)) return;
        if (ctx.Engine.State.CurrentPlayer != mySeat) 
        {
            await Clients.Caller.SendAsync("Error", "Not your turn");
            return;
        }

        // 2. Execute Roll Logic (Server Random)
        if (ctx.Engine.TryRollDice(out var result))
        {
            // 3. Save
            await roomService.SaveGameAsync(ctx);

            // 4. Broadcast
            // Send Roll result animation trigger
            await Clients.Group(roomId).SendAsync("RollResult", result.DiceValue);
            
            // If turn passed automatically (no moves), send state sync
            if (result.Status == LudoStatus.TurnPassed || result.Status == LudoStatus.ForfeitTurn)
            {
                await Clients.Group(roomId).SendAsync("GameState", SerializeState(ctx.Engine.State));
            }
        }
    }

    public async Task MoveToken(string roomId, int tokenIndex)
    {
        var ctx = await roomService.LoadGameAsync(roomId);
        if (ctx == null) return;

        if (!ctx.Meta.PlayerSeats.TryGetValue(UserId, out int mySeat)) return;
        if (ctx.Engine.State.CurrentPlayer != mySeat) return;

        if (ctx.Engine.TryMoveToken(tokenIndex, out var result))
        {
            await roomService.SaveGameAsync(ctx);
            
            // Broadcast the state. Clients will interpolate based on changes.
            // Alternatively, send specific move delta for smoother animation.
            await Clients.Group(roomId).SendAsync("GameState", SerializeState(ctx.Engine.State));
            
            if ((result.Status & LudoStatus.GameWon) != 0)
            {
                await Clients.Group(roomId).SendAsync("GameWon", UserId);
            }
        }
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