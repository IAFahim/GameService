using GameService.GameCore;
using GameService.ServiceDefaults.DTOs;
using System.Net.Http.Json;
using System.Text.Json;

namespace GameService.Web.Services;

public class GameAdminService(HttpClient http)
{
    public async Task<List<GameRoomDto>> GetActiveGamesAsync()
        => await http.GetFromJsonAsync<List<GameRoomDto>>("/admin/games") ?? [];

    public async Task<List<AdminPlayerDto>> GetPlayersAsync()
        => await http.GetFromJsonAsync<List<AdminPlayerDto>>("/admin/players") ?? [];

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

    public async Task<JsonElement?> GetGameStateAsync(string roomId)
    {
        try { return await http.GetFromJsonAsync<JsonElement>($"/admin/games/{roomId}"); } catch { return null; }
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
    
    public async Task<JsonElement?> GetLuckyMineFullStateAsync(string roomId)
    {
        try { return await http.GetFromJsonAsync<JsonElement>($"/admin/luckymine/{roomId}/state"); } catch { return null; }
    }

    public async Task<List<GameTemplateDto>> GetTemplatesAsync()
        => await http.GetFromJsonAsync<List<GameTemplateDto>>("/admin/templates") ?? [];

    public async Task CreateTemplateAsync(CreateTemplateRequest req)
    {
        var res = await http.PostAsJsonAsync("/admin/templates", req);
        res.EnsureSuccessStatusCode();
    }

    public async Task DeleteTemplateAsync(int id)
    {
        var res = await http.DeleteAsync($"/admin/templates/{id}");
        res.EnsureSuccessStatusCode();
    }

    public async Task<string?> CreateGameFromTemplateAsync(int templateId)
    {
        var res = await http.PostAsJsonAsync("/admin/games/create-from-template", new CreateRoomFromTemplateRequest(templateId));
        res.EnsureSuccessStatusCode();
        var content = await res.Content.ReadFromJsonAsync<JsonElement>();
        return content.TryGetProperty("roomId", out var p) ? p.GetString() : null;
    }

    public async Task<string?> CreateGameAsync(string gameType, int playerCount, long entryFee = 0, string? configJson = null)
    {
        var payload = new 
        { 
            GameType = gameType, 
            PlayerCount = playerCount,
            EntryFee = entryFee,
            ConfigJson = configJson
        };
        
        var response = await http.PostAsJsonAsync("/admin/games", payload);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadFromJsonAsync<JsonElement>();
        return content.TryGetProperty("roomId", out var p) ? p.GetString() : null;
    }
}