using GameService.GameCore;
using GameService.ServiceDefaults.Data;
using Microsoft.AspNetCore.Identity;

namespace GameService.ApiService.Features.Auth;

public class SystemUserValidator : IUserValidator<ApplicationUser>
{
    public Task<IdentityResult> ValidateAsync(UserManager<ApplicationUser> manager, ApplicationUser user)
    {
        if (string.Equals(user.UserName, GameCoreConstants.SystemUserId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(user.Email, GameCoreConstants.SystemUserId, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(IdentityResult.Failed(new IdentityError
            {
                Code = "ReservedUserName",
                Description = $"The username '{GameCoreConstants.SystemUserId}' is reserved."
            }));
        }

        return Task.FromResult(IdentityResult.Success);
    }
}
