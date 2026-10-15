using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Queueify.Web.Data;
using Queueify.Web.Sync;

namespace Queueify.Web.Services;

/// <summary>
/// Whether a new account may be created now: always for the first one (its owner), then only if
/// `Queueify:AllowRegistration` says so. "The first one" is remembered in a marker file in the data folder, so
/// deleting the last account (or losing the account database) does not hand the server to the next visitor.
/// To start over on purpose, delete <c>owner-exists</c> from the data folder together with the account database.
/// </summary>
public sealed class RegistrationGate(IConfiguration config, UserManager<ApplicationUser> users, DataLocation data)
{
    // One creation at a time per server, so "nobody exists yet" is still true when the account is written.
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    private string MarkerPath => Path.Combine(data.Root, "owner-exists");

    public async Task<bool> IsOpenAsync()
    {
        var ownerExists = File.Exists(MarkerPath);
        if (!ownerExists && await users.Users.AnyAsync())
        {
            // A server that ran before the marker existed.
            Mark();
            ownerExists = true;
        }
        return QueueifyUi.RegistrationOpen(config.GetValue("Queueify:AllowRegistration", false), ownerExists);
    }

    /// <summary>Runs <paramref name="create"/> only if registration is open, with the check and the write inside one lock.</summary>
    public async Task<bool> RunIfOpenAsync(Func<Task> create)
    {
        await OneAtATime.WaitAsync();
        try
        {
            if (!await IsOpenAsync()) return false;
            await create();
            if (await users.Users.AnyAsync()) Mark();
            return true;
        }
        finally { OneAtATime.Release(); }
    }

    private void Mark()
    {
        Directory.CreateDirectory(data.Root);
        File.WriteAllText(MarkerPath, "The first account of this server was created; registration stays closed unless Queueify:AllowRegistration is true.\n");
    }
}
