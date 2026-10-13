namespace Queueify.Models;

public sealed class AppSettings
{
    public string SpotifyClientId { get; set; } = "";
    public string SpotifyClientSecret { get; set; } = "";
    public string TidalClientId { get; set; } = "";
    public string TidalClientSecret { get; set; } = "";

    /// <summary>"Tidal" or "Spotify": which app Play opens when an album is on both.</summary>
    public string PreferredService { get; set; } = nameof(MusicService.Tidal);

    /// <summary>ISO 3166-1 alpha-2 country used for catalog availability.</summary>
    public string CountryCode { get; set; } = "AR";

    /// <summary>Last value typed in "Reading", reused for the next album from the same magazine.</summary>
    public string LastSource { get; set; } = "";

    /// <summary>When the shared settings above (keys, preferred app, country) last changed, for syncing.</summary>
    public DateTime? SharedSettingsUpdatedAt { get; set; }

    // ── Online sync (Google Firebase / Firestore) ──

    /// <summary>The user's Firebase project id.</summary>
    public string CloudProjectId { get; set; } = "";

    /// <summary>Long random key that names this queue online; only PCs that know it can read or change it.</summary>
    public string CloudQueueKey { get; set; } = "";

    /// <summary>Server time of the newest change already downloaded; the next sync asks only for newer ones.</summary>
    public DateTime? CloudLastServerTime { get; set; }

    /// <summary>A copy to try changes on (e.g. a sync connection) without touching the live settings.</summary>
    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    /// <summary>Takes the values that sync between PCs (keys, preferred app, country) from another copy.</summary>
    public void CopySharedFrom(AppSettings other)
    {
        SpotifyClientId = other.SpotifyClientId;
        SpotifyClientSecret = other.SpotifyClientSecret;
        TidalClientId = other.TidalClientId;
        TidalClientSecret = other.TidalClientSecret;
        PreferredService = other.PreferredService;
        CountryCode = other.CountryCode;
        SharedSettingsUpdatedAt = other.SharedSettingsUpdatedAt;
    }

    /// <summary>
    /// Applies what the user changed in the Settings dialog: only fields where <paramref name="edited"/>
    /// differs from <paramref name="opened"/> (the values shown when the dialog opened). Values a sync
    /// brought in while the dialog was open survive. Returns true if any shared value changed.
    /// </summary>
    public bool ApplyEdits(AppSettings opened, AppSettings edited)
    {
        bool changed = false;
        void Take(string before, string after, Action<string> set)
        {
            if (before == after) return;
            set(after);
            changed = true;
        }
        Take(opened.SpotifyClientId, edited.SpotifyClientId, v => SpotifyClientId = v);
        Take(opened.SpotifyClientSecret, edited.SpotifyClientSecret, v => SpotifyClientSecret = v);
        Take(opened.TidalClientId, edited.TidalClientId, v => TidalClientId = v);
        Take(opened.TidalClientSecret, edited.TidalClientSecret, v => TidalClientSecret = v);
        Take(opened.PreferredService, edited.PreferredService, v => PreferredService = v);
        Take(opened.CountryCode, edited.CountryCode, v => CountryCode = v);
        return changed;
    }

    public bool SpotifyConfigured =>
        !string.IsNullOrWhiteSpace(SpotifyClientId) && !string.IsNullOrWhiteSpace(SpotifyClientSecret);

    public bool TidalConfigured =>
        !string.IsNullOrWhiteSpace(TidalClientId) && !string.IsNullOrWhiteSpace(TidalClientSecret);

    public bool CloudConfigured =>
        !string.IsNullOrWhiteSpace(CloudProjectId) && !string.IsNullOrWhiteSpace(CloudQueueKey);
}
