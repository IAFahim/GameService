using GameService.GameCore;
using GameService.ServiceDefaults.DTOs;
using System.Net.Http.Json;

namespace GameService.Web.Services;

public class GameAdminService(HttpClient http)
{
    public async Task<List<GameRoomDto>> GetActiveGamesAsync()
    {
        return await http.GetFromJsonAsync<List<GameRoomDto>>("/admin/games") ?? [];
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
    
    public async Task PlayLudoRollAsync(string roomId)
    {
        var response = await http.PostAsync($"/admin/ludo/{roomId}/roll", null);
        response.EnsureSuccessStatusCode();
    }

    public async Task PlayLudoMoveAsync(string roomId, int tokenIndex)
    {
        var response = await http.PostAsync($"/admin/ludo/{roomId}/move/{tokenIndex}", null);
        response.EnsureSuccessStatusCode();
    }

    public async Task<System.Text.Json.JsonElement?> GetGameStateAsync(string roomId)
    {
        try 
        {
            return await http.GetFromJsonAsync<System.Text.Json.JsonElement>($"/admin/games/{roomId}");
        }
        catch
        {
            return null;
        }
    }

    public async Task DeletePlayerAsync(string userId)
    {
        var response = await http.DeleteAsync($"/admin/players/{userId}");
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteGameAsync(string roomId)
    {
        var response = await http.DeleteAsync($"/admin/games/{roomId}");
        response.EnsureSuccessStatusCode();
    }

    public async Task<List<SupportedGameDto>> GetSupportedGamesAsync()
    {
        return await http.GetFromJsonAsync<List<SupportedGameDto>>("/games/supported") ?? [];
    }
    
    public async Task CreateGameAsync(string gameType, int playerCount)
    {
        var response = await http.PostAsJsonAsync("/admin/games", new { GameType = gameType, PlayerCount = playerCount });
        response.EnsureSuccessStatusCode();
    }
    
    public async Task<System.Text.Json.JsonElement?> GetLuckyMineFullStateAsync(string roomId)
    {
        try 
        {
            return await http.GetFromJsonAsync<System.Text.Json.JsonElement>($"/admin/luckymine/{roomId}/state");
        }
        catch
        {
            return null;
        }
    }
}