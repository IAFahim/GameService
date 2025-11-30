namespace GameService.GameCore;

public interface IGameRoomService
{
    string GameType { get; }
    Task<string> CreateRoomAsync(string? hostUserId, int playerCount = 4);
    Task DeleteRoomAsync(string roomId);
    Task<bool> JoinRoomAsync(string roomId, string userId);
    Task<object?> GetGameStateAsync(string roomId);
    Task<List<GameRoomDto>> GetActiveGamesAsync();
}

public record GameRoomDto(string RoomId, string GameType, int PlayerCount, bool IsPublic, Dictionary<string, int> PlayerSeats);