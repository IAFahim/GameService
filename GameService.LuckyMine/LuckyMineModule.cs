using System.Text.Json.Serialization;
using GameService.GameCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace GameService.LuckyMine;

public sealed class LuckyMineModule : IGameModule
{
    public string GameName => "LuckyMine";
    public JsonSerializerContext? JsonContext => LuckyMineJsonContext.Default;

    public void RegisterServices(IServiceCollection services)
    {
        services.AddKeyedSingleton<IGameEngine, LuckyMineEngine>(GameName);
        services.AddKeyedSingleton<IGameRoomService, LuckyMineRoomService>(GameName);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/luckymine").RequireAuthorization("AdminPolicy");

        group.MapGet("/{roomId}/state", async (
            string roomId, 
            IGameRepositoryFactory repoFactory) =>
        {
            var repo = repoFactory.Create<LuckyMineState>(GameName);
            var ctx = await repo.LoadAsync(roomId);
            
            if (ctx == null) return Results.NotFound();

            var dto = new LuckyMineFullDto
            {
                RevealedMask0 = ctx.State.RevealedMask0,
                RevealedMask1 = ctx.State.RevealedMask1,
                JackpotCounter = ctx.State.JackpotCounter,
                CurrentPlayerIndex = ctx.State.CurrentPlayerIndex,
                TotalTiles = ctx.State.TotalTiles,
                RemainingMines = ctx.State.TotalMines,
                EntryCost = ctx.State.EntryCost,
                Status = ((LuckyMineStatus)ctx.State.Status).ToString(),
                MineMask0 = ctx.State.MineMask0,
                MineMask1 = ctx.State.MineMask1
            };
            
            return Results.Ok(dto);
        });
    }
}

[JsonSerializable(typeof(LuckyMineDto))]
[JsonSerializable(typeof(LuckyMineFullDto))]
[JsonSerializable(typeof(LuckyMineState))]
public partial class LuckyMineJsonContext : JsonSerializerContext { }