using System.Text.Json;

namespace GameService.Sdk.Core;

public sealed class GameCatalogClient
{
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _jsonOptions;

    public GameCatalogClient(HttpClient http)
    {
        _http = http;
        _jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    }

    public async Task<IReadOnlyList<GameTemplateDto>> GetTemplatesAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("/games/templates", ct);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<IReadOnlyList<GameTemplateDto>>(stream, _jsonOptions, ct) ?? Array.Empty<GameTemplateDto>();
    }

    public async Task<CreateRoomResult> CreateRoomFromTemplateAsync(int templateId, CancellationToken ct = default)
    {
        var content = new StringContent(
            JsonSerializer.Serialize(new CreateRoomFromTemplateRequest(templateId)),
            System.Text.Encoding.UTF8, 
            "application/json");

        using var response = await _http.PostAsync("/games/rooms", content, ct);
        
        if (response.IsSuccessStatusCode)
        {
            using var stream = await response.Content.ReadAsStreamAsync();
            var result = await JsonSerializer.DeserializeAsync<CreateRoomResponseInternal>(stream, _jsonOptions, ct);
            return new CreateRoomResult(true, result?.RoomId, null);
        }
        
        return new CreateRoomResult(false, null, await response.Content.ReadAsStringAsync());
    }

    public async Task<IReadOnlyList<GameRoomDto>> GetLobbyAsync(string gameType, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"/games/lobby?gameType={gameType}&page={page}&pageSize={pageSize}", ct);
        if (!response.IsSuccessStatusCode) return Array.Empty<GameRoomDto>();
        
        using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<IReadOnlyList<GameRoomDto>>(stream, _jsonOptions, ct) ?? Array.Empty<GameRoomDto>();
    }

    public async Task<QuickMatchResponse?> QuickMatchAsync(string gameType, int maxPlayers, long entryFee, CancellationToken ct = default)
    {
        var content = new StringContent(
            JsonSerializer.Serialize(new QuickMatchRequest(gameType, maxPlayers, entryFee)),
            System.Text.Encoding.UTF8, 
            "application/json");

        using var response = await _http.PostAsync("/games/quick-match", content, ct);
        if (!response.IsSuccessStatusCode) return null;

        using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<QuickMatchResponse>(stream, _jsonOptions, ct);
    }

    private record CreateRoomResponseInternal(string RoomId, string GameType);
}
