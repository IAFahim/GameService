namespace GameService.GameCore;

/// <summary>
///     Global registry mapping RoomId → GameType for O(1) lookups.
///     Eliminates the need to iterate through all game services.
/// </summary>
public interface IRoomRegistry
{
    /// <summary>
    ///     Get the game type for a room (O(1) lookup)
    /// </summary>
    Task<string?> GetGameTypeAsync(string roomId);

    /// <summary>
    ///     Register a new room with its game type
    /// </summary>
    Task RegisterRoomAsync(string roomId, string gameType);

    /// <summary>
    ///     Remove a room from the registry
    /// </summary>
    Task UnregisterRoomAsync(string roomId);

    /// <summary>
    ///     Get all active room IDs (for admin/monitoring)
    /// </summary>
    Task<IReadOnlyList<string>> GetAllRoomIdsAsync();

    /// <summary>
    ///     Get all active room IDs for a specific game type
    /// </summary>
    Task<IReadOnlyList<string>> GetRoomIdsByGameTypeAsync(string gameType);

    /// <summary>
    ///     Get paginated room IDs for a specific game type using cursor-based pagination
    /// </summary>
    Task<(IReadOnlyList<string> RoomIds, long NextCursor)> GetRoomIdsPagedAsync(string gameType, long cursor = 0,
        int pageSize = 50);

    /// <summary>
    ///     Acquire a distributed lock for a room
    /// </summary>
    Task<bool> TryAcquireLockAsync(string roomId, TimeSpan timeout);

    /// <summary>
    ///     Release a distributed lock for a room
    /// </summary>
    Task ReleaseLockAsync(string roomId);

    /// <summary>
    ///     Track which room a user is currently in (O(1) lookup on disconnect)
    /// </summary>
    Task SetUserRoomAsync(string userId, string roomId);

    /// <summary>
    ///     Get the room a user is currently in
    /// </summary>
    Task<string?> GetUserRoomAsync(string userId);

    /// <summary>
    ///     Remove user→room mapping when user leaves
    /// </summary>
    Task RemoveUserRoomAsync(string userId);
}