using GameService.Web.Data;
using Microsoft.AspNetCore.Identity;

namespace GameService.Web;

public sealed class IdentityUserAccessor(UserManager<ApplicationUser> userManager, IdentityRedirectManager redirectManager)
{
    public async Task<ApplicationUser> GetRequiredUserAsync(HttpContext context)
    {
        var user = await userManager.GetUserAsync(context.User);
        if (user is null)
        {
            redirectManager.RedirectTo("Account/Login");
        }
        return user!;
    }
}