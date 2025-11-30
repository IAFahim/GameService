using System.Runtime.CompilerServices;

namespace GameService.GameCore;

/// <summary>
/// Generic repository for game state persistence.
/// All games use the same repository implementation with different TState types.
/// </summary>
public interface IGameRepository<TState> where TState : struct
{
    Task<GameContext<TState>?> LoadAsync(string roomId);
    Task SaveAsync(string roomId, TState state, GameRoomMeta meta);
    Task DeleteAsync(string roomId);
    Task<bool> TryAcquireLockAsync(string roomId, TimeSpan timeout);
    Task ReleaseLockAsync(string roomId);
}

/// <summary>
/// Game context containing state and metadata
/// </summary>
public sealed record GameContext<TState>(string RoomId, TState State, GameRoomMeta Meta) 
    where TState : struct;

/// <summary>
/// Factory for creating typed game repositories
/// </summary>
public interface IGameRepositoryFactory
{
    IGameRepository<TState> Create<TState>(string gameType) where TState : struct;
}

/// <summary>
/// Marker interface for game states - enforces struct constraint at compile time
/// </summary>
public interface IGameState
{
    // Marker interface - all game states must be structs
}

/// <summary>
/// Validates game state structs at startup
/// </summary>
public static class GameStateValidator
{
    private const int MaxStateSizeBytes = 1024;
    
    public static void Validate<T>() where T : struct, IGameState
    {
        var size = Unsafe.SizeOf<T>();
        if (size > MaxStateSizeBytes)
        {
            throw new InvalidOperationException(
                $"Game state {typeof(T).Name} is {size} bytes, exceeding the {MaxStateSizeBytes} byte limit. " +
                "Consider using more compact data structures.");
        }
    }
}
