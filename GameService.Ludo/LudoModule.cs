using System.Text.Json.Serialization;
using GameService.GameCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace GameService.Ludo;

/// <summary>
/// Ludo game module - registers all Ludo-specific services with keyed DI.
/// </summary>
public sealed class LudoModule : IGameModule
{
    public string GameName => "Ludo";
    public Version Version => new(1, 0, 0);
    public JsonSerializerContext? JsonContext => LudoJsonContext.Default;

    public void RegisterServices(IServiceCollection services)
    {
        services.AddKeyedSingleton<IGameEngine, LudoGameEngine>(GameName);
        services.AddKeyedSingleton<IGameRoomService, LudoRoomService>(GameName);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var admin = endpoints.MapGroup("/admin/ludo").RequireAuthorization("AdminPolicy");

        admin.MapPost("/{roomId}/roll", async (
            string roomId, 
            IServiceProvider sp,
            IGameBroadcaster broadcaster) =>
        {
            var engine = sp.GetKeyedService<IGameEngine>(GameName);
            if (engine == null) return Results.NotFound("Ludo engine not available");

            var command = new GameCommand("ADMIN", "roll", default);
            var result = await engine.ExecuteAsync(roomId, command);
            
            if (result.Success)
            {
                await broadcaster.BroadcastResultAsync(roomId, result);
                return Results.Ok(result);
            }
            
            return Results.BadRequest(result);
        });

        admin.MapPost("/{roomId}/move/{tokenIndex:int}", async (
            string roomId, 
            int tokenIndex,
            IServiceProvider sp,
            IGameBroadcaster broadcaster) =>
        {
            var engine = sp.GetKeyedService<IGameEngine>(GameName);
            if (engine == null) return Results.NotFound("Ludo engine not available");

            var payload = System.Text.Json.JsonDocument.Parse($"{{\"tokenIndex\":{tokenIndex}}}").RootElement;
            var command = new GameCommand("ADMIN", "move", payload);
            var result = await engine.ExecuteAsync(roomId, command);
            
            if (result.Success)
            {
                await broadcaster.BroadcastResultAsync(roomId, result);
                return Results.Ok(result);
            }
            
            return Results.BadRequest(result);
        });
        
        admin.MapGet("/{roomId}/state", async (
            string roomId,
            IServiceProvider sp) =>
        {
            var engine = sp.GetKeyedService<IGameEngine>(GameName);
            if (engine == null)
                return Results.NotFound("Ludo engine not available");

            var state = await engine.GetStateAsync(roomId);
            return state != null ? Results.Ok(state) : Results.NotFound();
        });
    }
}