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
    private readonly Dictionary<string, string> _keys = [];   // uid -> normalised search text, worked out once per album
    private List<BacklogItem>? _pending, _heard;

    /// <summary>Raised after every change, so the navigation badges and open pages redraw.</summary>
    public event Action? Changed;

    public IReadOnlyList<BacklogItem> All => _all;
    public IReadOnlyList<BacklogItem> PendingItems => _pending ??= _all.Where(i => i.IsPending).ToList();
    public IReadOnlyList<BacklogItem> HeardItems => _heard ??= _all.Where(i => i.IsListened).ToList();
    public int Pending => PendingItems.Count;
    public int Heard => HeardItems.Count;

    /// <summary>Bumped on every change, so a page can tell whether what it worked out last time is still true.</summary>
    public int Version { get; private set; }

    /// <summary>
    /// The albums whose artist, album, magazine or year contain what was typed, as <see cref="QueueView"/> would find them
    /// (same normalisation), but from keys kept per album instead of normalising every album on every keystroke.
    /// Listened or not and the order are still <see cref="QueueView.Apply"/>'s job.
    /// </summary>
    public IReadOnlyList<BacklogItem> Search(string typed)
    {
        var words = Matcher.Normalize(typed);
        return words.Length == 0 ? _all : _all.Where(i => _keys.TryGetValue(i.Uid, out var key) && key.Contains(words)).ToList();
    }
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
        _keys.Clear();
        foreach (var item in _all) _keys[item.Uid] = KeyOf(item);
        Touch();
    }

    private static string KeyOf(BacklogItem i) => Matcher.Normalize($"{i.Artist} {i.Album} {i.Source} {i.Year}");

    private void Touch()
    {
        Version++;
        _pending = _heard = null;
        Changed?.Invoke();
    }

    /// <summary>Shows the stored version of one album (or none, if it is gone), and tells the pages.</summary>
    private void Show(string uid, BacklogItem? stored)
    {
        var at = _all.FindIndex(i => i.Uid == uid);
        if (stored is null or { Deleted: true })
        {
            if (at >= 0) _all.RemoveAt(at);
            _keys.Remove(uid);
        }
        else
        {
            if (at >= 0) _all[at] = stored; else _all.Add(stored);
            _keys[uid] = KeyOf(stored);
        }
        Touch();
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
        Show(item.Uid, item);
        return item;
    }

    /// <summary>Takes a freshly added album back out (the Undo on "Added …").</summary>
    public void Remove(BacklogItem item)
    {
        if (Store.FindByUid(item.Uid) is { Deleted: false } current) Store.Delete(current);
        Show(item.Uid, null);
    }

    // Every change starts from the stored album, not from the copy a page drew earlier: another tab may have
    // deleted, rated or listened to it since, and writing the old copy back would undo that. Only that one album is
    // updated in memory afterwards; reading the whole queue again is what LoadAsync is for.
    public Action SetListened(BacklogItem item, bool listened)
    {
        if (Store.FindByUid(item.Uid) is not { Deleted: false } current || current.IsListened == listened)
        {
            Show(item.Uid, Store.FindByUid(item.Uid));
            return () => { };
        }
        var before = current.ListenedAt;
        var stamp = listened ? DateTime.Now : (DateTime?)null;
        current.ListenedAt = stamp;
        Store.Update(current);
        Show(item.Uid, current);
        return () =>
        {
            if (Store.FindByUid(item.Uid) is not { Deleted: false } now || now.ListenedAt != stamp) return;
            now.ListenedAt = before;
            Store.Update(now);
            Show(item.Uid, now);
        };
    }

    public void Rate(BacklogItem item, double rating)
    {
        var current = Store.FindByUid(item.Uid);
        if (current is { Deleted: false } && current.Rating != rating)
        {
            current.Rating = rating;
            Store.Update(current);
        }
        Show(item.Uid, current);
    }

    public Action Delete(BacklogItem item)
    {
        Store.Delete(Store.FindByUid(item.Uid) ?? item);
        Show(item.Uid, null);
        return () =>
        {
            // The stored copy may be newer, or already gone after an upload (then it is created again).
            var back = Store.FindByUid(item.Uid) ?? item;
            Store.Restore(back);
            Show(item.Uid, back);
        };
    }
}
