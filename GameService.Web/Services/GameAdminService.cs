using System.Text.Json;
using GameService.GameCore;
using GameService.ServiceDefaults.Data;
using GameService.ServiceDefaults.DTOs;

namespace GameService.Web.Services;

public class GameAdminService(HttpClient http)
{
    public async Task<List<GlobalSetting>> GetSettingsAsync()
    {
        return await http.GetFromJsonAsync<List<GlobalSetting>>("/admin/settings") ?? [];
    }

    public async Task UpdateSettingAsync(string key, string value, string? description = null)
    {
        var response = await http.PutAsJsonAsync("/admin/settings", new { Key = key, Value = value, Description = description });
        response.EnsureSuccessStatusCode();
    }

    public async Task<DashboardStatsDto?> GetDashboardStatsAsync()
    {
        try
        {
            return await http.GetFromJsonAsync<DashboardStatsDto>("/admin/stats");
        }
        catch
        {
            return null;
        }
    }

    public async Task SendBroadcastAsync(string message)
    {
        var response = await http.PostAsJsonAsync("/admin/broadcast", new BroadcastRequest(message));
        response.EnsureSuccessStatusCode();
    }

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

    public async Task<JsonElement?> GetGameStateAsync(string roomId)
    {
        try
        {
            return await http.GetFromJsonAsync<JsonElement>($"/admin/games/{roomId}");
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

    public async Task<JsonElement?> GetLuckyMineFullStateAsync(string roomId)
    {
        try
        {
            return await http.GetFromJsonAsync<JsonElement>($"/admin/luckymine/{roomId}/full-state");
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<GameTemplateDto>> GetTemplatesAsync()
    {
        return await http.GetFromJsonAsync<List<GameTemplateDto>>("/admin/templates") ?? [];
    }

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
        var res = await http.PostAsJsonAsync("/admin/games/create-from-template",
            new CreateRoomFromTemplateRequest(templateId));
        res.EnsureSuccessStatusCode();
        var content = await res.Content.ReadFromJsonAsync<JsonElement>();
        return content.TryGetProperty("roomId", out var p) ? p.GetString() : null;
    }

    public async Task<string?> CreateGameAsync(string gameType, int playerCount, long entryFee = 0,
        string? configJson = null)
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

    public async Task<PagedResult<WalletTransactionDto>?> GetPlayerHistoryAsync(string userId, int page = 1, int pageSize = 20)
    {
        try
        {
            return await http.GetFromJsonAsync<PagedResult<WalletTransactionDto>>(
                $"/admin/players/{userId}/history?page={page}&pageSize={pageSize}");
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<DailyLoginAnalyticsDto>> GetDailyLoginAnalyticsAsync()
    {
        return await http.GetFromJsonAsync<List<DailyLoginAnalyticsDto>>("/admin/analytics/daily-login") ?? [];
    }

    public async Task<List<DailySpinAnalyticsDto>> GetDailySpinAnalyticsAsync()
    {
        return await http.GetFromJsonAsync<List<DailySpinAnalyticsDto>>("/admin/analytics/daily-spin") ?? [];
    }

    public async Task<List<GamePlayerDto>> GetGamePlayersAsync(string roomId)
    {
        return await http.GetFromJsonAsync<List<GamePlayerDto>>($"/admin/games/{roomId}/players") ?? [];
    }

    public async Task UpdatePlayerProfileAsync(string userId, AdminUpdateProfileRequest req)
    {
        var response = await http.PutAsJsonAsync($"/admin/players/{userId}/profile", req);
        response.EnsureSuccessStatusCode();
    }

    public async Task SaveGameEconomyConfigAsync(string gameType, GameEconomyConfig config)
    {
        var json = JsonSerializer.Serialize(config);
        await UpdateSettingAsync($"Game:{gameType}:Economy", json);
    }

    public async Task SaveDailyRewardsConfigAsync(string gameType, DailyRewardsWrapper config)
    {
        var json = JsonSerializer.Serialize(config);
        await UpdateSettingAsync($"Game:{gameType}:DailyRewards", json);
    }

    public async Task SaveSpinWheelConfigAsync(string gameType, SpinWheelConfig config)
    {
        var json = JsonSerializer.Serialize(config);
        await UpdateSettingAsync($"Game:{gameType}:SpinWheel", json);
    }

    public async Task<GameEconomyConfig> GetGameEconomyConfigAsync(string gameType)
    {
        var settings = await GetSettingsAsync();
        var setting = settings.FirstOrDefault(s => s.Key == $"Game:{gameType}:Economy");
        if (setting != null && !string.IsNullOrEmpty(setting.Value))
        {
            try { return JsonSerializer.Deserialize<GameEconomyConfig>(setting.Value) ?? new(); } catch {}
        }
        return new GameEconomyConfig();
    }

    public async Task<DailyRewardsWrapper> GetDailyRewardsConfigAsync(string gameType)
    {
        var settings = await GetSettingsAsync();
        var setting = settings.FirstOrDefault(s => s.Key == $"Game:{gameType}:DailyRewards");
        if (setting != null && !string.IsNullOrEmpty(setting.Value))
        {
            try { return JsonSerializer.Deserialize<DailyRewardsWrapper>(setting.Value) ?? new(); } catch {}
        }
        return new DailyRewardsWrapper();
    }

    public async Task<SpinWheelConfig> GetSpinWheelConfigAsync(string gameType)
    {
        var settings = await GetSettingsAsync();
        var setting = settings.FirstOrDefault(s => s.Key == $"Game:{gameType}:SpinWheel");
        if (setting != null && !string.IsNullOrEmpty(setting.Value))
        {
            try { return JsonSerializer.Deserialize<SpinWheelConfig>(setting.Value) ?? new(); } catch {}
        }
        return new SpinWheelConfig();
    }

    public async Task<GlobalEconomyConfig> GetGlobalEconomyConfigAsync()
    {
        try
        {
            return await http.GetFromJsonAsync<GlobalEconomyConfig>("/game/economy/config") 
                   ?? new GlobalEconomyConfig();
        }
        catch
        {
            return new GlobalEconomyConfig();
        }
    }
}

public class GlobalEconomyConfig
{
    public long InitialCoins { get; set; }
}

public class DailyRewardConfig
{
    public int Day { get; set; }
    public long Amount { get; set; }
    public string Type { get; set; } = "Coin";
    public bool IsJackpot { get; set; }
}

public class DailyRewardsWrapper
{
    public bool Enabled { get; set; }
    public List<DailyRewardConfig> Rewards { get; set; } = new();
}

public class SpinWheelSegment
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
    public long Amount { get; set; }
    public int Weight { get; set; }
    public string Color { get; set; } = "#CCCCCC";
}

public class SpinWheelConfig
{
    public bool Enabled { get; set; }
    public long CostPerSpin { get; set; }
    public int FreeSpinsPerDay { get; set; }
    public List<SpinWheelSegment> Segments { get; set; } = new();
}

public class GameEconomyConfig
{
    public long WelcomeBonus { get; set; }
    public bool DailyEnabled { get; set; }
}