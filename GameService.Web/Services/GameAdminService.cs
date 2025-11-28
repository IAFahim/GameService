using GameService.Ludo;
using GameService.ServiceDefaults.DTOs;
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

    public async Task<List<AdminPlayerDto>> GetPlayersAsync()
    {
        return await http.GetFromJsonAsync<List<AdminPlayerDto>>("/admin/players") ?? [];
    }

    public async Task UpdatePlayerCoinsAsync(string userId, long amount)
    {
        var response = await http.PostAsJsonAsync($"/admin/players/{userId}/coins", new UpdateCoinRequest(amount));
        response.EnsureSuccessStatusCode();
    }

    public async Task DeletePlayerAsync(string userId)
    {
        var response = await http.DeleteAsync($"/admin/players/{userId}");
        response.EnsureSuccessStatusCode();
    }
}