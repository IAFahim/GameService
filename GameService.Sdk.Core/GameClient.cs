using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

namespace GameService.Sdk.Core;

/// <summary>
/// 🎮 The main GameService client - your gateway to multiplayer gaming!
/// 
/// Quick start:
/// <code>
/// var client = await GameClient.ConnectAsync("https://api.example.com", "your-jwt-token");
/// client.OnGameState += state => Console.WriteLine($"Game updated!");
/// </code>
/// </summary>
public sealed class GameClient : IAsyncDisposable
{
    private readonly HubConnection _hub;
    private readonly string _baseUrl;
    private bool _disposed;

    /// <summary>Current connection state</summary>
    public ConnectionState State => _hub.State switch
    {
        HubConnectionState.Connected => ConnectionState.Connected,
        HubConnectionState.Connecting => ConnectionState.Connecting,
        HubConnectionState.Reconnecting => ConnectionState.Reconnecting,
        _ => ConnectionState.Disconnected
    };

    /// <summary>The room you're currently in (null if not in a room)</summary>
    public string? CurrentRoomId { get; private set; }

    // ═══════════════════════════════════════════════════════════════
    // 🎯 EVENTS - Subscribe to these for real-time updates!
    // ═══════════════════════════════════════════════════════════════

    /// <summary>🎲 Fired when game state changes - this is your main update loop!</summary>
    public event Action<GameState>? OnGameState;

    /// <summary>👋 A player joined the room</summary>
    public event Action<PlayerJoined>? OnPlayerJoined;

    /// <summary>🚪 A player left the room</summary>
    public event Action<PlayerLeft>? OnPlayerLeft;

    /// <summary>⚡ A player disconnected (they have a grace period to reconnect)</summary>
    public event Action<PlayerDisconnected>? OnPlayerDisconnected;

    /// <summary>🔌 A player reconnected within the grace period</summary>
    public event Action<PlayerReconnected>? OnPlayerReconnected;

    /// <summary>💬 Chat message received</summary>
    public event Action<ChatMessage>? OnChatMessage;

    /// <summary>🎯 Generic game event (dice rolls, moves, etc.)</summary>
    public event Action<GameEvent>? OnGameEvent;

    /// <summary>❌ An action you tried failed</summary>
    public event Action<ActionError>? OnActionError;

    /// <summary>🔄 Connection state changed</summary>
    public event Action<ConnectionState>? OnConnectionStateChanged;

    private GameClient(HubConnection hub, string baseUrl)
    {
        _hub = hub;
        _baseUrl = baseUrl;
        SetupEventHandlers();
    }

    // ═══════════════════════════════════════════════════════════════
    // 🚀 CONNECTION - Getting started
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 🔌 Connect to the game server!
    /// </summary>
    /// <param name="baseUrl">Server URL (e.g., "https://api.example.com")</param>
    /// <param name="accessToken">Your JWT access token from login</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>A connected GameClient ready for action!</returns>
    public static async Task<GameClient> ConnectAsync(
        string baseUrl,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        var hubUrl = baseUrl.TrimEnd('/') + "/gamehub";

        var hub = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
            })
            .WithAutomaticReconnect(new RetryPolicy())
            .Build();

        var client = new GameClient(hub, baseUrl);
        await hub.StartAsync(cancellationToken);
        return client;
    }

    /// <summary>
    /// 🔌 Create a client builder for advanced configuration
    /// </summary>
    public static GameClientBuilder Create(string baseUrl) => new(baseUrl);

    // ═══════════════════════════════════════════════════════════════
    // 🏠 ROOM MANAGEMENT - Create, join, leave rooms
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 🏗️ Create a new game room from a template
    /// </summary>
    /// <param name="templateName">Template name (e.g., "StandardLudo", "99Mines")</param>
    /// <returns>Result with RoomId on success</returns>
    public async Task<CreateRoomResult> CreateRoomAsync(string templateName)
    {
        EnsureConnected();
        var response = await _hub.InvokeAsync<CreateRoomResponse>("CreateRoom", templateName);
        
        if (response.Success && response.RoomId != null)
        {
            CurrentRoomId = response.RoomId;
        }
        
        return new CreateRoomResult(response.Success, response.RoomId, response.ErrorMessage);
    }

    /// <summary>
    /// 🚪 Join an existing game room
    /// </summary>
    /// <param name="roomId">The room's short ID</param>
    /// <returns>Result with your seat index on success</returns>
    public async Task<JoinRoomResult> JoinRoomAsync(string roomId)
    {
        EnsureConnected();
        var response = await _hub.InvokeAsync<JoinRoomResponse>("JoinRoom", roomId);
        
        if (response.Success)
        {
            CurrentRoomId = roomId;
        }
        
        return new JoinRoomResult(response.Success, response.SeatIndex, response.ErrorMessage);
    }

    /// <summary>
    /// 👋 Leave the current room
    /// </summary>
    public async Task LeaveRoomAsync()
    {
        if (CurrentRoomId == null) return;
        EnsureConnected();
        
        await _hub.InvokeAsync("LeaveRoom", CurrentRoomId);
        CurrentRoomId = null;
    }

    /// <summary>
    /// 👀 Spectate a room without playing
    /// </summary>
    public async Task<SpectateResult> SpectateAsync(string roomId)
    {
        EnsureConnected();
        var response = await _hub.InvokeAsync<SpectateRoomResponse>("SpectateRoom", roomId);
        return new SpectateResult(response.Success, response.ErrorMessage);
    }

    /// <summary>
    /// 🚶 Stop spectating
    /// </summary>
    public async Task StopSpectatingAsync(string roomId)
    {
        EnsureConnected();
        await _hub.InvokeAsync("StopSpectating", roomId);
    }

    // ═══════════════════════════════════════════════════════════════
    // 🎮 GAME ACTIONS - Play the game!
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// ⚡ Perform a game action (roll dice, move piece, reveal tile, etc.)
    /// </summary>
    /// <param name="actionName">Action name (e.g., "Roll", "Move", "Reveal")</param>
    /// <param name="payload">Action-specific data</param>
    /// <param name="commandId">Optional unique ID for idempotency</param>
    /// <returns>Result of the action</returns>
    public async Task<ActionResult> PerformActionAsync(
        string actionName,
        object? payload = null,
        string? commandId = null)
    {
        EnsureConnected();
        if (CurrentRoomId == null)
            return new ActionResult(false, "Not in a room", null);

        var jsonPayload = payload == null 
            ? JsonDocument.Parse("{}").RootElement 
            : JsonSerializer.SerializeToElement(payload);

        var response = await _hub.InvokeAsync<GameActionResponse>(
            "PerformAction", 
            CurrentRoomId, 
            actionName, 
            jsonPayload,
            commandId);

        return new ActionResult(response.Success, response.ErrorMessage, response.NewState);
    }

    /// <summary>
    /// 📋 Get legal actions you can take right now
    /// </summary>
    public async Task<IReadOnlyList<string>> GetLegalActionsAsync()
    {
        if (CurrentRoomId == null) return Array.Empty<string>();
        EnsureConnected();
        
        return await _hub.InvokeAsync<IReadOnlyList<string>>("GetLegalActions", CurrentRoomId);
    }

    /// <summary>
    /// 🔄 Manually refresh the game state
    /// </summary>
    public async Task<GameState?> GetStateAsync()
    {
        if (CurrentRoomId == null) return null;
        EnsureConnected();
        
        var response = await _hub.InvokeAsync<GameStateResponse?>("GetState", CurrentRoomId);
        return response == null ? null : MapGameState(response);
    }

    // ═══════════════════════════════════════════════════════════════
    // 💬 CHAT - Talk to other players
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 💬 Send a chat message to the room
    /// </summary>
    public async Task SendChatAsync(string message)
    {
        if (CurrentRoomId == null) return;
        EnsureConnected();
        
        await _hub.InvokeAsync("SendChatMessage", CurrentRoomId, message);
    }

    // ═══════════════════════════════════════════════════════════════
    // 🔧 INTERNAL HELPERS
    // ═══════════════════════════════════════════════════════════════

    private void SetupEventHandlers()
    {
        _hub.On<GameStateResponse>("GameState", response =>
        {
            OnGameState?.Invoke(MapGameState(response));
        });

        _hub.On<PlayerJoinedPayload>("PlayerJoined", payload =>
        {
            OnPlayerJoined?.Invoke(new PlayerJoined(payload.UserId, payload.UserName, payload.SeatIndex));
        });

        _hub.On<PlayerLeftPayload>("PlayerLeft", payload =>
        {
            OnPlayerLeft?.Invoke(new PlayerLeft(payload.UserId, payload.UserName));
        });

        _hub.On<PlayerDisconnectedPayload>("PlayerDisconnected", payload =>
        {
            OnPlayerDisconnected?.Invoke(new PlayerDisconnected(
                payload.UserId, payload.UserName, payload.GracePeriodSeconds));
        });

        _hub.On<PlayerReconnectedPayload>("PlayerReconnected", payload =>
        {
            OnPlayerReconnected?.Invoke(new PlayerReconnected(payload.UserId, payload.UserName));
        });

        _hub.On<ChatMessagePayload>("ChatMessage", payload =>
        {
            OnChatMessage?.Invoke(new ChatMessage(
                payload.UserId, payload.UserName, payload.Message, payload.Timestamp));
        });

        _hub.On<GameEventPayload>("GameEvent", payload =>
        {
            OnGameEvent?.Invoke(new GameEvent(payload.EventName, payload.Data, payload.Timestamp));
        });

        _hub.On<ActionErrorPayload>("ActionError", payload =>
        {
            OnActionError?.Invoke(new ActionError(payload.Action, payload.ErrorMessage));
        });

        _hub.Reconnecting += _ =>
        {
            OnConnectionStateChanged?.Invoke(ConnectionState.Reconnecting);
            return Task.CompletedTask;
        };

        _hub.Reconnected += _ =>
        {
            OnConnectionStateChanged?.Invoke(ConnectionState.Connected);
            return Task.CompletedTask;
        };

        _hub.Closed += _ =>
        {
            OnConnectionStateChanged?.Invoke(ConnectionState.Disconnected);
            return Task.CompletedTask;
        };
    }

    private static GameState MapGameState(GameStateResponse response) => new(
        response.RoomId,
        response.GameType,
        response.Phase,
        response.CurrentTurnUserId,
        response.Meta.CurrentPlayerCount,
        response.Meta.MaxPlayers,
        response.Meta.PlayerSeats,
        response.GameSpecificState);

    private void EnsureConnected()
    {
        if (_hub.State != HubConnectionState.Connected)
            throw new InvalidOperationException("Not connected to server");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        
        if (_hub.State == HubConnectionState.Connected)
        {
            await _hub.StopAsync();
        }
        await _hub.DisposeAsync();
    }

    private class RetryPolicy : IRetryPolicy
    {
        private static readonly TimeSpan[] Delays = 
        {
            TimeSpan.FromSeconds(0),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30)
        };

        public TimeSpan? NextRetryDelay(RetryContext retryContext)
        {
            return retryContext.PreviousRetryCount < Delays.Length 
                ? Delays[retryContext.PreviousRetryCount] 
                : TimeSpan.FromSeconds(60);
        }
    }
}

/// <summary>Builder for advanced GameClient configuration</summary>
public sealed class GameClientBuilder
{
    private readonly string _baseUrl;
    private string? _accessToken;
    private Action<HubConnectionBuilder>? _configure;

    internal GameClientBuilder(string baseUrl) => _baseUrl = baseUrl;

    /// <summary>Set the access token</summary>
    public GameClientBuilder WithAccessToken(string token)
    {
        _accessToken = token;
        return this;
    }

    /// <summary>Configure the underlying HubConnection</summary>
    public GameClientBuilder Configure(Action<HubConnectionBuilder> configure)
    {
        _configure = configure;
        return this;
    }

    /// <summary>Build and connect!</summary>
    public Task<GameClient> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_accessToken == null)
            throw new InvalidOperationException("Access token is required. Call WithAccessToken() first.");
        
        return GameClient.ConnectAsync(_baseUrl, _accessToken, cancellationToken);
    }
}
