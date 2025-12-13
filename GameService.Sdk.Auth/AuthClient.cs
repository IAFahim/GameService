namespace GameService.Sdk.Auth;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameService.Sdk.Core;

public sealed class AuthClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly JsonSerializerOptions _jsonOptions;
    private bool _disposed;

    public AuthClient(string baseUrl, HttpClient? httpClient = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = httpClient ?? new HttpClient();
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
    }

    public async Task<RegisterResult> RegisterAsync(string email, string password)
    {
        var request = new RegisterRequest(email, password);
        var content = new StringContent(
            JsonSerializer.Serialize(request, _jsonOptions),
            Encoding.UTF8,
            "application/json");

        var response = await _http.PostAsync($"{_baseUrl}/auth/register", content);

        if (response.IsSuccessStatusCode)
        {
            return new RegisterResult(true, null);
        }

        var errorBody = await response.Content.ReadAsStringAsync();
        return new RegisterResult(false, ParseError(errorBody));
    }

    public async Task<LoginResult> LoginAsync(string email, string password)
    {
        var request = new LoginRequest(email, password);
        var content = new StringContent(
            JsonSerializer.Serialize(request, _jsonOptions),
            Encoding.UTF8,
            "application/json");

        var response = await _http.PostAsync($"{_baseUrl}/auth/login", content);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            return new LoginResult(false, null, ParseError(errorBody));
        }

        var body = await response.Content.ReadAsStringAsync();
        var tokens = JsonSerializer.Deserialize<TokenResponse>(body, _jsonOptions);

        if (tokens?.AccessToken == null)
        {
            return new LoginResult(false, null, "Invalid response from server");
        }

        var session = new GameSession(_baseUrl, tokens.AccessToken, tokens.RefreshToken, this);
        return new LoginResult(true, session, null);
    }

    public async Task<RefreshResult> RefreshTokenAsync(string refreshToken)
    {
        var request = new RefreshRequest(refreshToken);
        var content = new StringContent(
            JsonSerializer.Serialize(request, _jsonOptions),
            Encoding.UTF8,
            "application/json");

        var response = await _http.PostAsync($"{_baseUrl}/auth/refresh", content);

        if (!response.IsSuccessStatusCode)
        {
            return new RefreshResult(false, null, null, "Token refresh failed");
        }

        var body = await response.Content.ReadAsStringAsync();
        var tokens = JsonSerializer.Deserialize<TokenResponse>(body, _jsonOptions);

        return tokens?.AccessToken != null
            ? new RefreshResult(true, tokens.AccessToken, tokens.RefreshToken, null)
            : new RefreshResult(false, null, null, "Invalid response");
    }

    public async Task<PlayerProfile?> GetProfileAsync(string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/game/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;

        var body = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<PlayerProfile>(body, _jsonOptions);
    }

    public async Task<long?> GetBalanceAsync(string accessToken)
    {
        var profile = await GetProfileAsync(accessToken);
        return profile?.Coins;
    }

    public async Task<ClaimResult> ClaimDailyLoginAsync(string accessToken)
    {
        return await ClaimRewardAsync(accessToken, "/game/daily-login");
    }

    public async Task<ClaimResult> ClaimDailySpinAsync(string accessToken)
    {
        return await ClaimRewardAsync(accessToken, "/game/daily-spin");
    }

    private async Task<ClaimResult> ClaimRewardAsync(string accessToken, string endpoint)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            return new ClaimResult(false, ParseError(body), 0, 0);
        }

        try 
        {
            var data = JsonSerializer.Deserialize<JsonElement>(body, _jsonOptions);
            long reward = 0;
            long newBalance = 0;
            
            if (data.TryGetProperty("reward", out var r)) reward = r.GetInt64();
            if (data.TryGetProperty("newBalance", out var nb)) newBalance = nb.GetInt64();
            
            return new ClaimResult(true, null, reward, newBalance);
        }
        catch
        {
            return new ClaimResult(false, "Failed to parse response", 0, 0);
        }
    }

    public async Task<PagedResult<WalletTransactionDto>?> GetTransactionHistoryAsync(string accessToken, int page = 1, int pageSize = 20)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/game/coins/history?page={page}&pageSize={pageSize}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;

        var body = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<PagedResult<WalletTransactionDto>>(body, _jsonOptions);
    }

    private static string ParseError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("errors", out var errors))
            {
                var messages = new List<string>();
                foreach (var prop in errors.EnumerateObject())
                {
                    foreach (var err in prop.Value.EnumerateArray())
                    {
                        messages.Add(err.GetString() ?? "Unknown error");
                    }
                }
                return string.Join("; ", messages);
            }
            if (doc.RootElement.TryGetProperty("detail", out var detail))
            {
                return detail.GetString() ?? "Unknown error";
            }
            if (doc.RootElement.TryGetProperty("title", out var title))
            {
                return title.GetString() ?? "Unknown error";
            }
        }
        catch { }
        return body.Length > 200 ? body[..200] : body;
    }

    public async Task<bool> LogoutAsync(string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/auth/logout");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await _http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}

public sealed class GameSession
{
    private readonly string _baseUrl;
    private readonly AuthClient _authClient;

    public string AccessToken { get; private set; }

    public string? RefreshToken { get; private set; }

    public bool IsValid => !string.IsNullOrEmpty(AccessToken);

    public CatalogClient Catalog { get; }

    internal GameSession(string baseUrl, string accessToken, string? refreshToken, AuthClient authClient)
    {
        _baseUrl = baseUrl;
        AccessToken = accessToken;
        RefreshToken = refreshToken;
        _authClient = authClient;
        
        Catalog = new CatalogClient(baseUrl, () => Task.FromResult<string?>(AccessToken));
    }

    public async Task LogoutAsync()
    {
        await _authClient.LogoutAsync(AccessToken);
        AccessToken = string.Empty;
    }

    public Task<GameClient> ConnectToGameAsync(CancellationToken cancellationToken = default)
    {
        return GameClient.ConnectAsync(
            _baseUrl, 
            async () => 
            {
                return AccessToken;
            }, 
            cancellationToken);
    }

    public async Task<bool> RefreshAsync()
    {
        if (RefreshToken == null) return false;

        var result = await _authClient.RefreshTokenAsync(RefreshToken);
        if (!result.Success) return false;

        AccessToken = result.AccessToken!;
        RefreshToken = result.RefreshToken;
        return true;
    }

    public Task<PlayerProfile?> GetProfileAsync() => _authClient.GetProfileAsync(AccessToken);

    public Task<long?> GetBalanceAsync() => _authClient.GetBalanceAsync(AccessToken);

    public Task<ClaimResult> ClaimDailyLoginAsync() => _authClient.ClaimDailyLoginAsync(AccessToken);

    public Task<ClaimResult> ClaimDailySpinAsync() => _authClient.ClaimDailySpinAsync(AccessToken);

    public Task<PagedResult<WalletTransactionDto>?> GetTransactionHistoryAsync(int page = 1, int pageSize = 20) => _authClient.GetTransactionHistoryAsync(AccessToken, page, pageSize);
}

public sealed record RegisterResult(bool Success, string? Error);

public sealed record LoginResult(bool Success, GameSession? Session, string? Error);

public sealed record RefreshResult(bool Success, string? AccessToken, string? RefreshToken, string? Error);

public sealed class PlayerProfile
{
    [JsonPropertyName("userId")]
    public string UserId { get; set; } = "";

    [JsonPropertyName("userName")]
    public string? UserName { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("coins")]
    public long Coins { get; set; }
}

internal sealed record RegisterRequest(string Email, string Password);
internal sealed record LoginRequest(string Email, string Password);
internal sealed record RefreshRequest(string RefreshToken);
internal sealed record TokenResponse(
    [property: JsonPropertyName("accessToken")] string? AccessToken,
    [property: JsonPropertyName("refreshToken")] string? RefreshToken,
    [property: JsonPropertyName("tokenType")] string? TokenType,
    [property: JsonPropertyName("expiresIn")] int ExpiresIn);
