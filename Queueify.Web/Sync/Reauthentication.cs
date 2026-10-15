using Microsoft.AspNetCore.Identity;
using Queueify.Web.Data;

namespace Queueify.Web.Sync;

/// <summary>
/// Asks for the password again before something that outlives the session is handed out (a sync token). A stolen
/// or left-open session therefore cannot mint one, and wrong guesses count towards the account's lockout like at sign-in.
/// </summary>
public sealed class Reauthentication(UserManager<ApplicationUser> users)
{
    public async Task<bool> ConfirmPasswordAsync(string userId, string? password)
    {
        if (string.IsNullOrEmpty(password) || await users.FindByIdAsync(userId) is not { } user) return false;
        if (await users.IsLockedOutAsync(user)) return false;
        if (await users.CheckPasswordAsync(user, password))
        {
            await users.ResetAccessFailedCountAsync(user);
            return true;
        }
        await users.AccessFailedAsync(user);
        return false;
    }
}
