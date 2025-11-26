using GameService.ApiService.Features.Common;
using GameService.ServiceDefaults.Data;
using GameService.ServiceDefaults.DTOs;
using Microsoft.EntityFrameworkCore;

namespace GameService.ApiService.Features.Players;

public interface IPlayerService
{
    Task<PlayerProfileResponse?> GetProfileAsync(string userId);
}

public class PlayerService(GameDbContext db, IGameEventPublisher publisher) : IPlayerService
{
    public async Task<PlayerProfileResponse?> GetProfileAsync(string userId)
    {
        var profile = await db.PlayerProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId);

        if (profile is null)
        {
            var newProfile = new PlayerProfile { UserId = userId, Coins = 100 };
            db.PlayerProfiles.Add(newProfile);
            try 
            {
                await db.SaveChangesAsync();
                profile = newProfile;
                
                var user = await db.Users.FindAsync(userId);
                var message = new PlayerUpdatedMessage(userId, profile.Coins, user?.UserName, user?.Email);
                await publisher.PublishPlayerUpdatedAsync(message);
            }
            catch (DbUpdateException) 
            {
                db.ChangeTracker.Clear();
                profile = await db.PlayerProfiles.AsNoTracking().FirstAsync(p => p.UserId == userId);
            }
        }
        
        return new PlayerProfileResponse(profile.UserId, profile.Coins);
    }
}