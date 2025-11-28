using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace GameService.Ludo;

[Authorize]
public class LudoHub(LudoRoomService roomService) : Hub
{
    private string UserId => Context.User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    public async Task CreateGame()
    {
        var roomId = await roomService.CreateRoomAsync(UserId);
        await Groups.AddToGroupAsync(Context.ConnectionId, roomId);

        await Clients.Caller.SendAsync("RoomCreated", roomId);
    }

    public async Task<bool> JoinGame(string roomId)
    {
        if (await roomService.JoinRoomAsync(roomId, UserId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, roomId);

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

        if (!ctx.Meta.PlayerSeats.TryGetValue(UserId, out int mySeat)) return;
        if (ctx.Engine.State.CurrentPlayer != mySeat) 
        {
            await Clients.Caller.SendAsync("Error", "Not your turn");
            return;
        }

        if (ctx.Engine.TryRollDice(out var result))
        {
            await roomService.SaveGameAsync(ctx);

            await Clients.Group(roomId).SendAsync("RollResult", result.DiceValue);

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

            await Clients.Group(roomId).SendAsync("GameState", SerializeState(ctx.Engine.State));
            
            if ((result.Status & LudoStatus.GameWon) != 0)
            {
                await Clients.Group(roomId).SendAsync("GameWon", UserId);
            }
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // In a real app, we would track connectionId -> roomId to notify "PlayerLeft"
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