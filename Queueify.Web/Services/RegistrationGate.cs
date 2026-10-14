using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Queueify.Web.Data;

namespace Queueify.Web.Services;

/// <summary>Whether a new account may be created now: always for the first one (its owner), then only if `Queueify:AllowRegistration` says so.</summary>
public sealed class RegistrationGate(IConfiguration config, UserManager<ApplicationUser> users)
{
    public async Task<bool> IsOpenAsync() =>
        QueueifyUi.RegistrationOpen(config.GetValue("Queueify:AllowRegistration", false), await users.Users.AnyAsync());
}
