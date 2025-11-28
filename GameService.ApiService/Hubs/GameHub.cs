using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace GameService.ApiService.Hubs;

[Authorize]
public class GameHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "Lobby");
        await base.OnConnectedAsync();
    }

    public async Task JoinRoom(string roomId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, roomId);
        await Clients.Group(roomId).SendAsync("PlayerJoined", Context.User?.Identity?.Name);
    }

    public async Task LeaveRoom(string roomId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId);
        await Clients.Group(roomId).SendAsync("PlayerLeft", Context.User?.Identity?.Name);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Note: We don't track which rooms the user was in, so we can't easily notify them here
        // without a connection mapping service. For now, we rely on explicit LeaveRoom or session timeout.
        await base.OnDisconnectedAsync(exception);
    }
    
    public async Task Ping(string message)
    {
        Console.WriteLine($"[Server] Received Ping: {message}");

        string response = $"Success: {message}";

        await Clients.Caller.SendAsync("Pong", response);
    }
}