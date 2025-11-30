# GameService Architecture Rewrite for 200+ Game Types

This document provides a complete architectural blueprint for rewriting GameService to support 200+ game types with O(1) lookups, unified infrastructure, and AOT compatibility.

---

## 📐 Target Architecture Overview

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                              CLIENTS                                        │
│   (Web, Mobile, Desktop - Single SignalR Connection per Client)            │
└─────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                         UNIFIED GAME HUB                                    │
│   GameHub.PerformAction(roomId, actionName, payload)                       │
│   - Routes to correct IGameEngine via O(1) keyed lookup                    │
│   - Single endpoint for ALL 200 games                                       │
└─────────────────────────────────────────────────────────────────────────────┘
                                    │
                    ┌───────────────┼───────────────┐
                    ▼               ▼               ▼
            ┌───────────┐   ┌───────────┐   ┌───────────┐
            │ LudoEngine│   │ChessEngine│   │PokerEngine│  ... (200 engines)
            └───────────┘   └───────────┘   └───────────┘
                    │               │               │
                    └───────────────┼───────────────┘
                                    ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                    GENERIC STATE REPOSITORY                                 │
│   RedisGameRepository<TState> - Single implementation for ALL games        │
│   + Global Room Registry (RoomId → GameType mapping)                       │
└─────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                         REDIS CLUSTER                                       │
│   Sharded by game type: {game:ludo}:{roomId}, {game:chess}:{roomId}        │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## 🔴 PHASE 1: Kill O(N) Lookups → O(1) with Keyed Services

### Current Anti-Pattern (AdminEndpoints.cs)
```csharp
// ❌ BAD: O(N) iteration - 200 games = 200 checks per request
private static async Task<IResult> CreateGame(
    [FromBody] CreateGameRequest req,
    IEnumerable<IGameRoomService> services)
{
    var service = services.FirstOrDefault(s => 
        s.GameType.Equals(req.GameType, StringComparison.OrdinalIgnoreCase));
}

// ❌ WORSE: O(N) for every state lookup
private static async Task<IResult> GetGameState(
    string roomId, 
    IEnumerable<IGameRoomService> services)
{
    foreach (var service in services)  // 200 services × Redis call each = disaster
    {
        var state = await service.GetGameStateAsync(roomId);
        if (state != null) return Results.Ok(state);
    }
}
```

### New Architecture: Keyed Services (.NET 8+)

**Step 1: Define the Registry Interface**
```csharp
// GameService.GameCore/IGameRegistry.cs
public interface IGameRegistry
{
    IGameEngine? GetEngine(string gameType);
    IGameRoomService? GetRoomService(string gameType);
    IEnumerable<string> GetSupportedGameTypes();
}
```

**Step 2: Register Games with Keys**
```csharp
// Each game module registers itself with a key
public class LudoModule : IGameModule
{
    public string GameName => "Ludo";
    
    public void RegisterServices(IServiceCollection services)
    {
        // Keyed registration - O(1) lookup
        services.AddKeyedSingleton<IGameEngine, LudoEngine>("Ludo");
        services.AddKeyedSingleton<IGameRoomService, LudoRoomService>("Ludo");
    }
}
```

**Step 3: O(1) Lookup in Endpoints**
```csharp
// ✅ GOOD: O(1) lookup
private static async Task<IResult> CreateGame(
    [FromBody] CreateGameRequest req,
    [FromKeyedServices("gameType")] IGameRoomService? service)
{
    // Or use IServiceProvider directly for dynamic keys:
}

// Even better - inject IServiceProvider for dynamic resolution
private static async Task<IResult> CreateGame(
    [FromBody] CreateGameRequest req,
    IServiceProvider sp)
{
    var service = sp.GetKeyedService<IGameRoomService>(req.GameType);
    if (service == null) 
        return Results.BadRequest($"Unknown game type: {req.GameType}");
    
    var roomId = await service.CreateRoomAsync(null, req.PlayerCount);
    return Results.Ok(new { RoomId = roomId });
}
```

**Step 4: Global Room Registry for State Lookups**
```csharp
// GameService.GameCore/IRoomRegistry.cs
public interface IRoomRegistry
{
    Task<string?> GetGameTypeAsync(string roomId);
    Task RegisterRoomAsync(string roomId, string gameType);
    Task UnregisterRoomAsync(string roomId);
}

// Implementation using Redis Hash
public class RedisRoomRegistry(IConnectionMultiplexer redis) : IRoomRegistry
{
    private const string RegistryKey = "global:room_registry";
    
    public async Task<string?> GetGameTypeAsync(string roomId)
    {
        var db = redis.GetDatabase();
        return await db.HashGetAsync(RegistryKey, roomId);
    }
    
    public async Task RegisterRoomAsync(string roomId, string gameType)
    {
        var db = redis.GetDatabase();
        await db.HashSetAsync(RegistryKey, roomId, gameType);
    }
}
```

**Step 5: Fixed GetGameState - Single Redis Call**
```csharp
private static async Task<IResult> GetGameState(
    string roomId, 
    IRoomRegistry registry,
    IServiceProvider sp)
{
    // 1. O(1) lookup: What game type is this room?
    var gameType = await registry.GetGameTypeAsync(roomId);
    if (gameType == null) return Results.NotFound("Room not found");
    
    // 2. O(1) lookup: Get the correct service
    var service = sp.GetKeyedService<IGameRoomService>(gameType);
    if (service == null) return Results.NotFound("Game type not supported");
    
    // 3. Single call to get state
    var state = await service.GetGameStateAsync(roomId);
    return Results.Ok(state);
}
```

---

## 🔵 PHASE 2: Unified SignalR Hub (Kill LudoHub, ChessHub, etc.)

### Current Anti-Pattern
```csharp
// ❌ BAD: One hub per game type = 200 hub classes
[Authorize]
public class LudoHub(LudoRoomService roomService) : Hub { ... }

// Client must know which URL to connect to:
// /hubs/ludo, /hubs/chess, /hubs/poker...
```

### New Architecture: Single GameHub

```csharp
// GameService.ApiService/Hubs/GameHub.cs
[Authorize]
public class GameHub(
    IRoomRegistry roomRegistry,
    IServiceProvider serviceProvider,
    ILogger<GameHub> logger) : Hub
{
    private string UserId => Context.User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    /// <summary>
    /// Universal action handler for ALL game types
    /// </summary>
    public async Task<GameActionResult> PerformAction(
        string roomId, 
        string actionName, 
        JsonElement payload)
    {
        // 1. Resolve game type from room
        var gameType = await roomRegistry.GetGameTypeAsync(roomId);
        if (gameType == null)
            return GameActionResult.Error("Room not found");

        // 2. Get the engine (O(1))
        var engine = serviceProvider.GetKeyedService<IGameEngine>(gameType);
        if (engine == null)
            return GameActionResult.Error("Game engine not available");

        // 3. Execute the action
        var command = new GameCommand(UserId, actionName, payload);
        var result = await engine.ExecuteAsync(roomId, command);

        // 4. Broadcast state if needed
        if (result.ShouldBroadcast)
        {
            await Clients.Group(roomId).SendAsync("GameState", result.NewState);
        }

        // 5. Broadcast events (DiceRolled, TokenMoved, etc.)
        foreach (var evt in result.Events)
        {
            await Clients.Group(roomId).SendAsync(evt.EventName, evt.Data);
        }

        return result;
    }

    public async Task<bool> JoinRoom(string roomId)
    {
        var gameType = await roomRegistry.GetGameTypeAsync(roomId);
        if (gameType == null) return false;

        var service = serviceProvider.GetKeyedService<IGameRoomService>(gameType);
        if (service == null) return false;

        if (await service.JoinRoomAsync(roomId, UserId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, roomId);
            await Clients.Group(roomId).SendAsync("PlayerJoined", UserId);
            return true;
        }
        return false;
    }

    public async Task LeaveRoom(string roomId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId);
        await Clients.Group(roomId).SendAsync("PlayerLeft", UserId);
    }
}
```

### Unified Game Engine Interface

```csharp
// GameService.GameCore/IGameEngine.cs
public interface IGameEngine
{
    string GameType { get; }
    
    /// <summary>
    /// Handle any action - Roll, Move, Bet, Draw, etc.
    /// </summary>
    Task<GameActionResult> ExecuteAsync(string roomId, GameCommand command);
    
    /// <summary>
    /// Get legal actions for current player
    /// </summary>
    Task<IReadOnlyList<string>> GetLegalActionsAsync(string roomId, string userId);
}

public record GameCommand(string UserId, string Action, JsonElement Payload);

public record GameActionResult(
    bool Success,
    string? ErrorMessage,
    bool ShouldBroadcast,
    object? NewState,
    IReadOnlyList<GameEvent> Events)
{
    public static GameActionResult Error(string message) => 
        new(false, message, false, null, []);
}

public record GameEvent(string EventName, object Data);
```

### Ludo Engine Refactored

```csharp
// GameService.Ludo/LudoGameEngine.cs
public class LudoGameEngine(
    IGameRepository<LudoState> repository,
    IDiceRoller diceRoller) : IGameEngine
{
    public string GameType => "Ludo";

    public async Task<GameActionResult> ExecuteAsync(string roomId, GameCommand command)
    {
        return command.Action.ToLowerInvariant() switch
        {
            "roll" => await HandleRollAsync(roomId, command.UserId),
            "move" => await HandleMoveAsync(roomId, command.UserId, command.Payload),
            _ => GameActionResult.Error($"Unknown action: {command.Action}")
        };
    }

    private async Task<GameActionResult> HandleRollAsync(string roomId, string userId)
    {
        var ctx = await repository.LoadAsync(roomId);
        if (ctx == null) return GameActionResult.Error("Room not found");

        var engine = new LudoEngine(ctx.State, diceRoller);
        
        if (!engine.TryRollDice(out var result))
            return GameActionResult.Error($"Cannot roll: {result.Status}");

        await repository.SaveAsync(roomId, engine.State);

        return new GameActionResult(
            Success: true,
            ErrorMessage: null,
            ShouldBroadcast: true,
            NewState: engine.State,
            Events: [new GameEvent("DiceRolled", new { Value = result.DiceValue })]
        );
    }
}
```

---

## 🟢 PHASE 3: Generic State Repository

### Current Anti-Pattern
```csharp
// ❌ BAD: Copy-paste for every game
public class RedisLudoRepository { ... }
public class RedisChessRepository { ... }  // Duplicate
public class RedisPokerRepository { ... }  // Duplicate
```

### New Architecture: Generic Repository

```csharp
// GameService.GameCore/IGameRepository.cs
public interface IGameRepository<TState> where TState : struct
{
    Task<GameContext<TState>?> LoadAsync(string roomId);
    Task SaveAsync(string roomId, TState state);
    Task DeleteAsync(string roomId);
    Task<bool> TryAcquireLockAsync(string roomId, TimeSpan timeout);
    Task ReleaseLockAsync(string roomId);
}

public record GameContext<TState>(string RoomId, TState State, GameRoomMeta Meta) 
    where TState : struct;

public record GameRoomMeta
{
    public Dictionary<string, int> PlayerSeats { get; init; } = new();
    public bool IsPublic { get; init; }
    public string GameType { get; init; } = "";
    public int MaxPlayers { get; init; }
}
```

```csharp
// GameService.Infrastructure/RedisGameRepository.cs
public class RedisGameRepository<TState>(
    IConnectionMultiplexer redis,
    IRoomRegistry roomRegistry,
    string gameType) : IGameRepository<TState> 
    where TState : struct
{
    private readonly IDatabase _db = redis.GetDatabase();
    
    // Use hash tags for Redis Cluster sharding
    private string StateKey(string roomId) => $"{{game:{gameType}}}:{roomId}:state";
    private string MetaKey(string roomId) => $"{{game:{gameType}}}:{roomId}:meta";
    private string LockKey(string roomId) => $"{{game:{gameType}}}:{roomId}:lock";

    public async Task<GameContext<TState>?> LoadAsync(string roomId)
    {
        var batch = _db.CreateBatch();
        var stateTask = batch.StringGetAsync(StateKey(roomId));
        var metaTask = batch.StringGetAsync(MetaKey(roomId));
        batch.Execute();
        
        await Task.WhenAll(stateTask, metaTask);
        
        if (stateTask.Result.IsNullOrEmpty) return null;
        
        var state = DeserializeState((byte[])stateTask.Result!);
        var meta = JsonSerializer.Deserialize<GameRoomMeta>(metaTask.Result!);
        
        return new GameContext<TState>(roomId, state, meta!);
    }

    public async Task SaveAsync(string roomId, TState state)
    {
        var bytes = SerializeState(state);
        await _db.StringSetAsync(StateKey(roomId), bytes);
    }

    public async Task<bool> TryAcquireLockAsync(string roomId, TimeSpan timeout)
    {
        return await _db.StringSetAsync(
            LockKey(roomId), 
            Environment.MachineName, 
            timeout, 
            When.NotExists);
    }

    // Struct serialization - platform-safe with explicit layout
    private static byte[] SerializeState(TState state)
    {
        var bytes = new byte[Unsafe.SizeOf<TState>()];
        Unsafe.WriteUnaligned(ref bytes[0], state);
        return bytes;
    }
    
    private static TState DeserializeState(byte[] bytes)
    {
        return Unsafe.ReadUnaligned<TState>(ref bytes[0]);
    }
}
```

### Factory Pattern for Repository Creation

```csharp
// GameService.GameCore/IGameRepositoryFactory.cs
public interface IGameRepositoryFactory
{
    IGameRepository<TState> Create<TState>(string gameType) where TState : struct;
}

public class RedisGameRepositoryFactory(
    IConnectionMultiplexer redis,
    IRoomRegistry roomRegistry) : IGameRepositoryFactory
{
    public IGameRepository<TState> Create<TState>(string gameType) where TState : struct
    {
        return new RedisGameRepository<TState>(redis, roomRegistry, gameType);
    }
}
```

---

## 🟡 PHASE 4: Frontend Dynamic Components

### Current Anti-Pattern (GameDetail.razor)
```razor
@* ❌ BAD: If-else for every game type *@
@if (isLudo)
{
    <LudoAdminPanel Game="ludoContext!" OnRefresh="LoadGame" />
}
else if (isChess)
{
    <ChessAdminPanel ... />
}
else if (isPoker)
{
    <PokerAdminPanel ... />
}
@* ... 197 more else-ifs *@
```

### New Architecture: Dynamic Component Resolution

**Step 1: Define Component Interface**
```csharp
// GameService.GameCore/IGameAdminComponent.cs
public interface IGameAdminComponent
{
    // Marker interface - components implement this
}
```

**Step 2: Component Registry**
```csharp
// GameService.Web/Services/GameComponentRegistry.cs
public class GameComponentRegistry
{
    private readonly Dictionary<string, Type> _components = new(StringComparer.OrdinalIgnoreCase);

    public void Register<TComponent>(string gameType) 
        where TComponent : IGameAdminComponent
    {
        _components[gameType] = typeof(TComponent);
    }

    public Type? GetComponentType(string gameType)
    {
        return _components.TryGetValue(gameType, out var type) ? type : null;
    }
}
```

**Step 3: Each Game Registers Its Component**
```csharp
// GameService.Ludo/LudoModule.cs (Web portion)
public static class LudoWebExtensions
{
    public static void RegisterLudoComponents(this GameComponentRegistry registry)
    {
        registry.Register<LudoAdminPanel>("Ludo");
    }
}
```

**Step 4: Clean GameDetail.razor**
```razor
@page "/games/{RoomId}"
@inject GameComponentRegistry ComponentRegistry
@inject GameAdminService AdminService

<div class="container-fluid">
    @if (isLoading)
    {
        <div class="spinner-border"></div>
    }
    else if (componentType != null)
    {
        @* ✅ GOOD: Dynamic component - works for ALL games *@
        <DynamicComponent Type="@componentType" Parameters="@GetParameters()" />
    }
    else
    {
        <div class="alert alert-info">
            <p>No visual inspector for @gameType. Raw state:</p>
            <pre>@rawJson</pre>
        </div>
    }
</div>

@code {
    [Parameter] public string RoomId { get; set; } = "";
    
    private Type? componentType;
    private string? gameType;
    private JsonElement? gameState;
    private bool isLoading = true;
    private string? rawJson;

    protected override async Task OnInitializedAsync()
    {
        var state = await AdminService.GetGameStateAsync(RoomId);
        if (state.HasValue)
        {
            gameState = state.Value;
            gameType = state.Value.GetProperty("meta").GetProperty("gameType").GetString();
            componentType = ComponentRegistry.GetComponentType(gameType ?? "");
            rawJson = state.Value.GetRawText();
        }
        isLoading = false;
    }

    private Dictionary<string, object?> GetParameters() => new()
    {
        ["RoomId"] = RoomId,
        ["GameState"] = gameState,
        ["OnRefresh"] = EventCallback.Factory.Create(this, LoadGame)
    };

    private async Task LoadGame() { /* reload */ }
}
```

---

## 🟣 PHASE 5: AOT-Compatible Module Registration

### Current Anti-Pattern
```csharp
// ❌ BAD: Reflection at runtime - breaks AOT
var modules = AppDomain.CurrentDomain.GetAssemblies()
    .SelectMany(a => a.GetTypes())
    .Where(t => typeof(IGameModule).IsAssignableFrom(t))
    .Select(Activator.CreateInstance)
    .Cast<IGameModule>();
```

### New Architecture: Source Generator or Explicit Registration

**Option A: Explicit Registration (Recommended for <50 games)**
```csharp
// GameService.ApiService/Program.cs
builder.Services.AddGamePlatform(games =>
{
    games.AddGame<LudoModule>();
    games.AddGame<ChessModule>();
    games.AddGame<PokerModule>();
    // Compile-time checked, AOT-safe
});
```

**Option B: Source Generator (For 200 games)**
```csharp
// Create a source generator that scans for [GameModule] attribute
[GameModule("Ludo")]
public partial class LudoModule : IGameModule { }

// Generated code (at compile time):
public static class GeneratedGameModules
{
    public static void RegisterAll(IServiceCollection services)
    {
        services.AddGameModule<LudoModule>("Ludo");
        services.AddGameModule<ChessModule>("Chess");
        // ... auto-generated for all 200
    }
}
```

**Option C: Assembly Scanning with Trimming Hints**
```csharp
// For AOT, use [DynamicallyAccessedMembers]
public static void RegisterGameModules(
    IServiceCollection services,
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] 
    params Type[] moduleTypes)
{
    foreach (var type in moduleTypes)
    {
        var module = (IGameModule)Activator.CreateInstance(type)!;
        module.RegisterServices(services);
    }
}

// Call with explicit types
RegisterGameModules(services, typeof(LudoModule), typeof(ChessModule));
```

---

## 🔶 PHASE 6: Event-Driven Game Loop

### Standard Game Command/Event Model

```csharp
// All games emit standardized events for:
// - Audit logging
// - Replay system
// - Spectator mode
// - Analytics

public interface IGameEvent
{
    string EventType { get; }
    DateTimeOffset Timestamp { get; }
    string RoomId { get; }
    int TurnNumber { get; }
}

public record DiceRolledEvent(
    string RoomId, 
    int TurnNumber, 
    int PlayerId, 
    int Value) : IGameEvent
{
    public string EventType => "DiceRolled";
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public record TokenMovedEvent(
    string RoomId,
    int TurnNumber,
    int PlayerId,
    int TokenIndex,
    int FromPosition,
    int ToPosition) : IGameEvent
{
    public string EventType => "TokenMoved";
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
```

### Event Store for Replays

```csharp
public interface IGameEventStore
{
    Task AppendAsync(string roomId, IGameEvent evt);
    IAsyncEnumerable<IGameEvent> GetEventsAsync(string roomId, int fromTurn = 0);
}

public class RedisGameEventStore(IConnectionMultiplexer redis) : IGameEventStore
{
    public async Task AppendAsync(string roomId, IGameEvent evt)
    {
        var db = redis.GetDatabase();
        var json = JsonSerializer.Serialize(evt, evt.GetType());
        await db.StreamAddAsync($"events:{roomId}", 
            [new NameValueEntry("data", json), new NameValueEntry("type", evt.EventType)]);
    }
}
```

---

## 📊 Memory Layout Requirements for 200 Games

### Enforce Struct-Based State

```csharp
// All game states MUST be structs with explicit layout
public interface IGameState
{
    // Marker interface
}

// Compile-time validation
public static class GameStateValidator
{
    public static void Validate<T>() where T : struct, IGameState
    {
        if (!typeof(T).IsValueType)
            throw new InvalidOperationException($"{typeof(T).Name} must be a struct");
        
        if (Unsafe.SizeOf<T>() > 1024)
            throw new InvalidOperationException($"{typeof(T).Name} exceeds 1KB limit");
    }
}
```

### Example: Properly Structured Game State

```csharp
[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct ChessState : IGameState
{
    [FieldOffset(0)] public ChessBoardBuffer Board;      // 32 bytes
    [FieldOffset(32)] public byte CurrentPlayer;          // 1 byte
    [FieldOffset(33)] public byte CastlingRights;         // 1 byte
    [FieldOffset(34)] public byte EnPassantSquare;        // 1 byte
    [FieldOffset(35)] public byte HalfMoveClock;          // 1 byte
    [FieldOffset(36)] public ushort FullMoveNumber;       // 2 bytes
    [FieldOffset(38)] public GameStatus Status;           // 1 byte
    [FieldOffset(39)] private byte _padding;              // Alignment
    [FieldOffset(40)] public int LastMoveFrom;            // 4 bytes
    [FieldOffset(44)] public int LastMoveTo;              // 4 bytes
    // ... remaining 16 bytes for extensions
}

[InlineArray(32)]
public struct ChessBoardBuffer
{
    private byte _element0;
}
```

---

## 📋 Migration Checklist

| Phase | File | Action |
|-------|------|--------|
| 1 | `AdminEndpoints.cs` | Remove `IEnumerable<IGameRoomService>`, use `IServiceProvider.GetKeyedService` |
| 1 | `IGameRoomService.cs` | Add `[ServiceKey]` attribute support |
| 1 | Create `IRoomRegistry.cs` | New file for RoomId → GameType mapping |
| 2 | `LudoHub.cs` | **DELETE** - merge into unified `GameHub.cs` |
| 2 | Create `GameHub.cs` | Single hub with `PerformAction(roomId, action, payload)` |
| 2 | `IGameEngine.cs` | Refactor to command/event pattern |
| 3 | `RedisLudoRepository.cs` | Replace with `RedisGameRepository<LudoState>` |
| 3 | Create `IGameRepository<T>.cs` | Generic repository interface |
| 4 | `GameDetail.razor` | Replace if-else with `<DynamicComponent>` |
| 4 | Create `GameComponentRegistry.cs` | Map GameType → Component Type |
| 5 | `Program.cs` | Replace reflection with explicit/generated registration |
| 6 | Create `IGameEvent.cs` | Standard event interface for all games |

---

## 🚀 Performance Targets

| Metric | Current | Target |
|--------|---------|--------|
| Game type lookup | O(N) | O(1) |
| Room state lookup | O(N) Redis calls | O(2) Redis calls |
| Hub classes | N (one per game) | 1 (unified) |
| Repository classes | N (one per game) | 1 (generic) |
| Startup time (200 games) | ~5s (reflection) | <500ms (generated) |
| Memory per game state | Variable (classes) | Fixed <1KB (structs) |

---

## 🔒 Security Fixes (Immediate)

### 1. Remove Hardcoded Secrets
```diff
- "ApiKey": "SecretAdminKey123!"
+ "ApiKey": "" // Load from Azure Key Vault / User Secrets
```

### 2. Restrict CORS
```csharp
policy.WithOrigins(
    "https://yourdomain.com",
    "https://admin.yourdomain.com")
    .AllowCredentials();
```

### 3. Add Rate Limiting Per Game Type
```csharp
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("RealTimeGame", context =>
        RateLimitPartition.GetSlidingWindowLimiter(
            context.User.Identity?.Name ?? context.Request.Headers.Host.ToString(),
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromSeconds(1),
                SegmentsPerWindow = 6
            }));
    
    options.AddPolicy("TurnBasedGame", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.Identity?.Name ?? "",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1)
            }));
});
```

---

## 🗑️ Dead Code to Remove

| File | Reason |
|------|--------|
| `GameService.ArchitectureTests/UnitTest1.cs` | Empty placeholder |
| `GameService.ComponentTests/UnitTest1.cs` | Empty placeholder |
| `GameService.UnitTests/UnitTest1.cs` | Empty placeholder |
| `PlayerService.PublishPlayerUpdatedAsync` in GET | Side effect on read operation |
| `LudoHub.cs` | Replaced by unified GameHub |

---

## 📈 Observability Additions

```csharp
// Add game-specific metrics
public class GameMetrics
{
    private readonly Counter<long> _gamesCreated;
    private readonly Counter<long> _movesExecuted;
    private readonly Histogram<double> _actionLatency;

    public GameMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create("GameService.Games");
        _gamesCreated = meter.CreateCounter<long>("games.created");
        _movesExecuted = meter.CreateCounter<long>("games.moves");
        _actionLatency = meter.CreateHistogram<double>("games.action.latency");
    }

    public void RecordGameCreated(string gameType)
    {
        _gamesCreated.Add(1, new KeyValuePair<string, object?>("game_type", gameType));
    }
}
```

---

*This architecture supports 200+ game types with O(1) operations, AOT compatibility, and clean separation of concerns.*
