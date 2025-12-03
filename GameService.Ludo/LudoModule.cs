using System.Text.Json;
using System.Text.Json.Serialization;
using GameService.GameCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace GameService.Ludo;

public sealed class LudoModule : IGameModule
{
    public string GameName => "Ludo";
    public JsonSerializerContext? JsonContext => LudoJsonContext.Default;

    public void RegisterServices(IServiceCollection services)
    {
        services.AddKeyedSingleton<IGameEngine, LudoGameEngine>(GameName);
        services.AddKeyedSingleton<IGameRoomService, LudoRoomService>(GameName);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var admin = endpoints.MapGroup("/admin/ludo").RequireAuthorization("AdminPolicy");

        // 1. Roll Endpoint
        admin.MapPost("/{roomId}/roll", async (string roomId, IServiceProvider sp, IGameBroadcaster bc) =>
        {
            var command = new GameCommand("ADMIN", "roll", default);
            var res = await sp.GetRequiredKeyedService<IGameEngine>("Ludo").ExecuteAsync(roomId, command);
            
            if (res.Success) await bc.BroadcastResultAsync(roomId, res);
            return res.Success ? Results.Ok(res) : Results.BadRequest(res);
        });

        // 2. Move Endpoint (WAS MISSING -> CAUSED 404)
        admin.MapPost("/{roomId}/move/{tokenIndex:int}", async (string roomId, int tokenIndex, IServiceProvider sp, IGameBroadcaster bc) =>
        {
            // Construct payload manually since GameCommand expects a JsonElement
            var json = $"{{\"tokenIndex\": {tokenIndex}}}";
            var payload = JsonDocument.Parse(json).RootElement;
            
            var command = new GameCommand("ADMIN", "move", payload);
            var res = await sp.GetRequiredKeyedService<IGameEngine>("Ludo").ExecuteAsync(roomId, command);

            if (res.Success) await bc.BroadcastResultAsync(roomId, res);
            return res.Success ? Results.Ok(res) : Results.BadRequest(res);
        });
    }
}