using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameService.Sdk.Core;

namespace GameService.Sdk.Auth;

/// <summary>
/// 🔐 Authentication client for GameService
/// 
/// Quick start:
/// <code>
/// var auth = new AuthClient("https://api.example.com");
/// 
/// // Register a new account
/// var result = await auth.RegisterAsync("player@email.com", "MyP@ssw0rd!");
/// 
/// // Or login to existing account
/// var session = await auth.LoginAsync("player@email.com", "MyP@ssw0rd!");
/// 
/// // Get a connected game client!
/// var gameClient = await session.ConnectToGameAsync();
/// </code>
/// </summary>
public sealed class AuthClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly JsonSerializerOptions _jsonOptions;
    private bool _disposed;

    /// <summary>Create an auth client for the given server</summary>
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

    // ═══════════════════════════════════════════════════════════════
    // 🔐 AUTHENTICATION
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 📝 Register a new account
    /// </summary>
    /// <param name="email">Email address</param>
    /// <param name="password">Password (min 8 chars, requires uppercase, lowercase, digit, special char)</param>
    /// <returns>Registration result</returns>
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

    /// <summary>
    /// 🔓 Login to an existing account
    /// </summary>
    /// <param name="email">Email address</param>
    /// <param name="password">Password</param>
    /// <returns>Session with tokens on success</returns>
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

    /// <summary>
    /// 🔄 Refresh an expired access token
    /// </summary>
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

    // ═══════════════════════════════════════════════════════════════
    // 👤 PLAYER INFO (requires authentication)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 👤 Get current player's profile
    /// </summary>
    public async Task<PlayerProfile?> GetProfileAsync(string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/players/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;

        var body = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<PlayerProfile>(body, _jsonOptions);
    }

    /// <summary>
    /// 💰 Get current coin balance
    /// </summary>
    public async Task<long?> GetBalanceAsync(string accessToken)
    {
        var profile = await GetProfileAsync(accessToken);
        return profile?.Coins;
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}

/// <summary>
/// 🎮 An authenticated game session - use this to connect to games!
/// </summary>
public sealed class GameSession
{
    private readonly string _baseUrl;
    private readonly AuthClient _authClient;

    /// <summary>Your current access token (JWT)</summary>
    public string AccessToken { get; private set; }

    /// <summary>Refresh token for getting new access tokens</summary>
    public string? RefreshToken { get; private set; }

    /// <summary>Whether the session has valid tokens</summary>
    public bool IsValid => !string.IsNullOrEmpty(AccessToken);

    internal GameSession(string baseUrl, string accessToken, string? refreshToken, AuthClient authClient)
    {
        _baseUrl = baseUrl;
        AccessToken = accessToken;
        RefreshToken = refreshToken;
        _authClient = authClient;
    }

    /// <summary>
    /// 🎮 Connect to the game server with this session!
    /// </summary>
    public Task<GameClient> ConnectToGameAsync(CancellationToken cancellationToken = default)
    {
        return GameClient.ConnectAsync(_baseUrl, AccessToken, cancellationToken);
    }

    /// <summary>
    /// 🔄 Refresh the access token if it's expired
    /// </summary>
    public async Task<bool> RefreshAsync()
    {
        if (RefreshToken == null) return false;

        var result = await _authClient.RefreshTokenAsync(RefreshToken);
        if (!result.Success) return false;

        AccessToken = result.AccessToken!;
        RefreshToken = result.RefreshToken;
        return true;
    }

    /// <summary>
    /// 👤 Get your player profile
    /// </summary>
    public Task<PlayerProfile?> GetProfileAsync() => _authClient.GetProfileAsync(AccessToken);

    /// <summary>
    /// 💰 Get your coin balance
    /// </summary>
    public Task<long?> GetBalanceAsync() => _authClient.GetBalanceAsync(AccessToken);
}

// ═══════════════════════════════════════════════════════════════
// 📦 TYPES
// ═══════════════════════════════════════════════════════════════

/// <summary>Registration result</summary>
public sealed record RegisterResult(bool Success, string? Error);

/// <summary>Login result</summary>
public sealed record LoginResult(bool Success, GameSession? Session, string? Error);

/// <summary>Token refresh result</summary>
public sealed record RefreshResult(bool Success, string? AccessToken, string? RefreshToken, string? Error);

/// <summary>Player profile</summary>
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

// Internal request/response types
internal sealed record RegisterRequest(string Email, string Password);
internal sealed record LoginRequest(string Email, string Password);
internal sealed record RefreshRequest(string RefreshToken);
internal sealed record TokenResponse(
    [property: JsonPropertyName("accessToken")] string? AccessToken,
    [property: JsonPropertyName("refreshToken")] string? RefreshToken,
    [property: JsonPropertyName("tokenType")] string? TokenType,
    [property: JsonPropertyName("expiresIn")] int ExpiresIn);
