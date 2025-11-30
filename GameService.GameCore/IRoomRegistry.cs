namespace GameService.GameCore;

/// <summary>
/// Global registry mapping RoomId → GameType for O(1) lookups.
/// Eliminates the need to iterate through all game services.
/// </summary>
public interface IRoomRegistry
{
    /// <summary>
    /// Get the game type for a room (O(1) lookup)
    /// </summary>
    Task<string?> GetGameTypeAsync(string roomId);
    
    /// <summary>
    /// Register a new room with its game type
    /// </summary>
    Task RegisterRoomAsync(string roomId, string gameType);
    
    /// <summary>
    /// Remove a room from the registry
    /// </summary>
    Task UnregisterRoomAsync(string roomId);
    
    /// <summary>
    /// Get all active room IDs (for admin/monitoring)
    /// </summary>
    Task<IReadOnlyList<string>> GetAllRoomIdsAsync();
    
    /// <summary>
    /// Get all active room IDs for a specific game type
    /// </summary>
    Task<IReadOnlyList<string>> GetRoomIdsByGameTypeAsync(string gameType);
}
