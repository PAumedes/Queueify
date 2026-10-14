using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Queueify.Web.Data;

namespace Queueify.Web.Services;

/// <summary>Whether a new account may be created now: always for the first one (its owner), then only if `Queueify:AllowRegistration` says so.</summary>
public sealed class RegistrationGate(IConfiguration config, UserManager<ApplicationUser> users)
{
    // One creation at a time per server, so "nobody exists yet" is still true when the account is written.
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    public async Task<bool> IsOpenAsync() =>
        QueueifyUi.RegistrationOpen(config.GetValue("Queueify:AllowRegistration", false), await users.Users.AnyAsync());

    /// <summary>Runs <paramref name="create"/> only if registration is open, with the check and the write inside one lock.</summary>
    public async Task<bool> RunIfOpenAsync(Func<Task> create)
    {
        await OneAtATime.WaitAsync();
        try
        {
            if (!await IsOpenAsync()) return false;
            await create();
            return true;
        }
        finally { OneAtATime.Release(); }
    }
}