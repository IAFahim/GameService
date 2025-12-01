using GameService.ServiceDefaults.Data;
using GameService.ServiceDefaults.DTOs;
using Microsoft.EntityFrameworkCore;

namespace GameService.ApiService.Features.Players;

public interface IPlayerService
{
    Task<PlayerProfileResponse?> GetProfileAsync(string userId);
}

public class PlayerService(GameDbContext db) : IPlayerService
{
    public async Task<PlayerProfileResponse?> GetProfileAsync(string userId)
    {
        var profile = await db.PlayerProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId);

        return profile is not null 
            ? new PlayerProfileResponse(profile.UserId, profile.Coins) 
            : null;
    }
}