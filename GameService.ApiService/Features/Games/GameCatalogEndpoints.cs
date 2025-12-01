using GameService.GameCore;
using GameService.ServiceDefaults.DTOs;

namespace GameService.ApiService.Features.Games;

public static class GameCatalogEndpoints
{
    public static void MapGameCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/games/supported", GetSupportedGames)
           .WithName("GetSupportedGames");
    }

    private static IResult GetSupportedGames(IEnumerable<IGameModule> modules)
    {
        var games = modules.Select(m => new SupportedGameDto(m.GameName)).ToList();
        return Results.Ok(games);
    }
}