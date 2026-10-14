using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Queueify.Models;
using Queueify.Services;
using Queueify.Web.Storage;

namespace Queueify.Web.Services;

/// <summary>Who is signed in, as the folder name their data lives under.</summary>
public interface IUserContext
{
    Task<string?> UserIdAsync();
}

public sealed class AuthUserContext(AuthenticationStateProvider auth) : IUserContext
{
    public async Task<string?> UserIdAsync()
    {
        var user = (await auth.GetAuthenticationStateAsync()).User;
        return user.Identity?.IsAuthenticated == true ? user.FindFirstValue(ClaimTypes.NameIdentifier) : null;
    }
}

/// <summary>
/// The signed-in user's queue for one circuit or request. Every change goes to that user's SQLite file
/// at once; undo re-reads the stored album first so a change made in another tab is never overwritten.
/// </summary>
public sealed class UserQueue(UserStores stores, IUserContext user)
{
    private string? _userId;
    private BacklogStore? _store;
    private List<BacklogItem> _all = [];

    /// <summary>Raised after every change, so the navigation badges and open pages redraw.</summary>
    public event Action? Changed;

    public IReadOnlyList<BacklogItem> All => _all;
    public int Pending => _all.Count(i => i.IsPending);
    public int Heard => _all.Count(i => i.IsListened);
    public bool SignedIn => _userId != null;

    /// <summary>Re-reads the queue from the database; nobody signed in means an empty queue.</summary>
    public async Task LoadAsync()
    {
        _userId = await user.UserIdAsync();
        _store = _userId == null ? null : stores.QueueOf(_userId);
        Settings = _userId == null ? new AppSettings() : stores.LoadSettings(_userId);
        Reload();
    }

    /// <summary>The user's settings; secrets stay on the server, so callers pass on only what they show.</summary>
    public AppSettings Settings { get; private set; } = new();

    /// <summary>Which app Play opens when an album is on both; each user chooses, so it is never a static.</summary>
    public MusicService Preferred => Settings.PreferredService == nameof(MusicService.Spotify) ? MusicService.Spotify : MusicService.Tidal;

    public string? UserId => _userId;

    private BacklogStore Store => _store ?? throw new InvalidOperationException("Nobody is signed in.");

    private void Reload()
    {
        _all = _store?.LoadAll() ?? [];
        Changed?.Invoke();
    }

    public BacklogItem Add(BacklogItem item, string source)
    {
        var magazine = source.Trim();
        item.Source = magazine.Length > 0 ? magazine : null;
        item.AddedAt = DateTime.Now;
        Store.Insert(item);

        // Read fresh: the Settings page may have saved new keys since this circuit loaded.
        var settings = stores.LoadSettings(_userId!);
        if (settings.LastSource != magazine)
        {
            settings.LastSource = magazine;
            stores.SaveSettings(_userId!, settings);
            Settings = settings;
        }
        Reload();
        return item;
    }

    /// <summary>Takes a freshly added album back out (the Undo on "Added …").</summary>
    public void Remove(BacklogItem item)
    {
        if (Store.FindByUid(item.Uid) is { Deleted: false } current) Store.Delete(current);
        Reload();
    }

    public Action SetListened(BacklogItem item, bool listened)
    {
        var before = item.ListenedAt;
        item.ListenedAt = listened ? DateTime.Now : null;
        Store.Update(item);
        Reload();
        return () =>
        {
            if (Store.FindByUid(item.Uid) is not { Deleted: false } current) return;
            current.ListenedAt = before;
            Store.Update(current);
            Reload();
        };
    }

    public void Rate(BacklogItem item, double rating)
    {
        if (item.Rating == rating) return;
        item.Rating = rating;
        Store.Update(item);
        Reload();
    }

    public Action Delete(BacklogItem item)
    {
        Store.Delete(item);
        Reload();
        return () =>
        {
            // The stored copy may be newer, or already gone after an upload (then it is created again).
            Store.Restore(Store.FindByUid(item.Uid) ?? item);
            Reload();
        };
    }
}
