namespace GameService.GameCore;

/// <summary>
/// Room service interface for room lifecycle management.
/// Each game type registers ONE room service with a keyed service.
/// </summary>
public interface IGameRoomService
{
    string GameType { get; }

    /// <summary>
    /// Create a new game room
    /// </summary>
    Task<string> CreateRoomAsync(string? hostUserId, int playerCount = 4);

    /// <summary>
    /// Delete a game room
    /// </summary>
    Task DeleteRoomAsync(string roomId);

    /// <summary>
    /// Join an existing room
    /// </summary>
    Task<JoinRoomResult> JoinRoomAsync(string roomId, string userId);

    /// <summary>
    /// Leave a room
    /// </summary>
    Task LeaveRoomAsync(string roomId, string userId);

    /// <summary>
    /// Get room metadata
    /// </summary>
    Task<GameRoomMeta?> GetRoomMetaAsync(string roomId);
}

/// <summary>
/// Result of attempting to join a room
/// </summary>
public sealed record JoinRoomResult(bool Success, string? ErrorMessage = null, int SeatIndex = -1)
{
    public static JoinRoomResult Ok(int seatIndex) => new(true, null, seatIndex);
    public static JoinRoomResult Error(string message) => new(false, message);
}

/// <summary>
/// DTO for listing active games
/// </summary>
public sealed record GameRoomDto(
    string RoomId,
    string GameType,
    int PlayerCount,
    int MaxPlayers,
    bool IsPublic,
    IReadOnlyDictionary<string, int> PlayerSeats);