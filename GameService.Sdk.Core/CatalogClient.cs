using System.Net.Http.Json;
using System.Text.Json;

namespace GameService.Sdk.Core;

public sealed class CatalogClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly Func<Task<string?>> _accessTokenProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    public CatalogClient(string baseUrl, Func<Task<string?>> accessTokenProvider, HttpClient? httpClient = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _accessTokenProvider = accessTokenProvider;
        _http = httpClient ?? new HttpClient();
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
    }

    public async Task<List<SupportedGameDto>> GetSupportedGamesAsync()
    {
        return await _http.GetFromJsonAsync<List<SupportedGameDto>>($"{_baseUrl}/games/supported", _jsonOptions) 
               ?? new List<SupportedGameDto>();
    }

    public async Task<List<GameTemplateDto>> GetTemplatesAsync()
    {
        await AddAuthHeaderAsync();
        return await _http.GetFromJsonAsync<List<GameTemplateDto>>($"{_baseUrl}/games/templates", _jsonOptions) 
               ?? new List<GameTemplateDto>();
    }

    public async Task<List<GameRoomDto>> GetLobbyAsync(string gameType, int page = 1, int pageSize = 20)
    {
        await AddAuthHeaderAsync();
        var uri = $"{_baseUrl}/games/lobby?gameType={Uri.EscapeDataString(gameType)}&page={page}&pageSize={pageSize}";
        return await _http.GetFromJsonAsync<List<GameRoomDto>>(uri, _jsonOptions) 
               ?? new List<GameRoomDto>();
    }

    public async Task<QuickMatchResponse?> QuickMatchAsync(string gameType, int maxPlayers = 4, long entryFee = 0, int? templateId = null)
    {
        await AddAuthHeaderAsync();
        var request = new QuickMatchRequest(gameType, maxPlayers, entryFee) { TemplateId = templateId };
        
        var response = await _http.PostAsJsonAsync($"{_baseUrl}/games/quick-match", request, _jsonOptions);
        if (!response.IsSuccessStatusCode) return null;

        return await response.Content.ReadFromJsonAsync<QuickMatchResponse>(_jsonOptions);
    }

    public async Task<CreateRoomResponseHttp?> CreateRoomFromTemplateAsync(int templateId)
    {
        await AddAuthHeaderAsync();
        var request = new CreateRoomFromTemplateRequest(templateId);
        
        var response = await _http.PostAsJsonAsync($"{_baseUrl}/games/rooms", request, _jsonOptions);
        if (!response.IsSuccessStatusCode) return null;

        return await response.Content.ReadFromJsonAsync<CreateRoomResponseHttp>(_jsonOptions);
    }

    private async Task AddAuthHeaderAsync()
    {
        var token = await _accessTokenProvider();
        if (!string.IsNullOrEmpty(token))
        {
            _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }
    }
}
