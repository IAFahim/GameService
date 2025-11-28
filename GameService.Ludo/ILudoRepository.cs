namespace GameService.Ludo;

public interface ILudoRepository
{
    Task SaveGameAsync(LudoContext context);
    Task<LudoContext?> LoadGameAsync(string roomId);
    Task<List<LudoContext>> GetActiveGamesAsync();
    Task DeleteGameAsync(string roomId);
    Task<bool> TryJoinRoomAsync(string roomId, string userId);
    Task AddActiveGameAsync(string roomId);
}