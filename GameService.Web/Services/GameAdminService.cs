using GameService.Ludo;
using System.Net.Http.Json;

namespace GameService.Web.Services;

public class GameAdminService(HttpClient http)
{
    public async Task<List<LudoContext>> GetActiveGamesAsync()
    {
        return await http.GetFromJsonAsync<List<LudoContext>>("/admin/games") ?? [];
    }

    public async Task CreateGameAsync()
    {
        var response = await http.PostAsync("/admin/games", null);
        response.EnsureSuccessStatusCode();
    }

    public async Task ForceRollAsync(string roomId, int value)
    {
        var response = await http.PostAsync($"/admin/games/{roomId}/roll?value={value}", null);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteGameAsync(string roomId)
    {
        var response = await http.DeleteAsync($"/admin/games/{roomId}");
        response.EnsureSuccessStatusCode();
    }
}