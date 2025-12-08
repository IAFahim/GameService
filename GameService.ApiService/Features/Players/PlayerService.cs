using GameService.GameCore;
using GameService.ServiceDefaults.Data;
using GameService.ServiceDefaults.DTOs;
using Microsoft.EntityFrameworkCore;

namespace GameService.ApiService.Features.Players;

public interface IPlayerService
{
    Task<PlayerProfileResponse?> GetProfileAsync(string userId);
}

public class PlayerService(GameDbContext db, IRoomRegistry roomRegistry) : IPlayerService
{
    public async Task<PlayerProfileResponse?> GetProfileAsync(string userId)
    {
        var profile = await db.PlayerProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId);

        if (profile is null) return null;

        var activeRoomId = await roomRegistry.GetUserRoomAsync(userId);
        string? gameType = null;

        if (!string.IsNullOrEmpty(activeRoomId))
        {
            gameType = await roomRegistry.GetGameTypeAsync(activeRoomId);
        }

        return new PlayerProfileResponse(
            profile.UserId,
            profile.Coins,
            activeRoomId,
            gameType
        );
    }
}