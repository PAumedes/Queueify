using System.Text.Json.Serialization;
using Queueify.Services;

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

    /// <summary>Last value typed in "Magazine", reused for the next album from the same magazine.</summary>
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

    // ── Online sync (a Queueify web server, ADR-0010). Per PC, never shared between PCs. ──

    /// <summary>The server's address (https, or http on loopback), from the connect text.</summary>
    public string WebServerUrl { get; set; } = "";

    /// <summary>This PC's personal access token. It lives only here and in the sync request's Authorization header.</summary>
    public string WebToken { get; set; } = "";

    /// <summary>The server's time of the last answer; the next sync asks only for newer changes.</summary>
    public DateTime? WebLastServerTime { get; set; }

    /// <summary>The shared-settings stamp already in step with the server, so unchanged keys aren't sent again.</summary>
    public DateTime? WebSettingsSyncedAt { get; set; }

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

    /// <summary>
    /// Gives hand-edited or older values their standard form: a two-letter upper-case country
    /// (Argentina when unusable) and "Tidal" or "Spotify".
    /// </summary>
    public void Normalize()
    {
        var country = CountryCode.Trim().ToUpperInvariant();
        CountryCode = country.Length == 2 && country.All(char.IsAsciiLetterUpper) ? country : "AR";
        PreferredService = PreferredService.Trim().Equals(nameof(MusicService.Spotify), StringComparison.OrdinalIgnoreCase)
            ? nameof(MusicService.Spotify)
            : nameof(MusicService.Tidal);
    }

    /// <summary>
    /// What the Settings page shows and compares edits against: a normalized copy, so picking the
    /// value already shown ("ar" shown as Argentina, handed back as "AR") is no change.
    /// </summary>
    public AppSettings ForSettingsPage()
    {
        var shown = Clone();
        shown.Normalize();
        return shown;
    }

    public bool SpotifyConfigured =>
        !string.IsNullOrWhiteSpace(SpotifyClientId) && !string.IsNullOrWhiteSpace(SpotifyClientSecret);

    public bool TidalConfigured =>
        !string.IsNullOrWhiteSpace(TidalClientId) && !string.IsNullOrWhiteSpace(TidalClientSecret);

    public bool CloudConfigured =>
        !string.IsNullOrWhiteSpace(CloudProjectId) && !string.IsNullOrWhiteSpace(CloudQueueKey);

    [JsonIgnore]
    public bool WebConfigured =>
        !string.IsNullOrWhiteSpace(WebServerUrl) && !string.IsNullOrWhiteSpace(WebToken);

    [JsonIgnore]
    public bool SyncConfigured => WebConfigured || CloudConfigured;

    /// <summary>The provider that syncs this PC. A PC uses one; the web server wins if a hand-edited file sets both.</summary>
    [JsonIgnore]
    public SyncKind ActiveSync => WebConfigured ? SyncKind.Web : CloudConfigured ? SyncKind.Cloud : SyncKind.None;

    /// <summary>
    /// Names the online queue in use, to tell whether a sync that was running still applies. Not saved: it
    /// would write the token a second time.
    /// </summary>
    [JsonIgnore]
    public string SyncIdentity => ActiveSync switch
    {
        SyncKind.Web => $"web|{WebServerUrl}|{WebToken}",
        SyncKind.Cloud => $"cloud|{CloudProjectId}|{CloudQueueKey}",
        _ => "",
    };

    /// <summary>Starts using this Firestore queue; a PC syncs with one place, so a web connection is dropped.</summary>
    public void ConnectCloud(string projectId, string queueKey)
    {
        DisconnectWeb();
        CloudProjectId = projectId;
        CloudQueueKey = queueKey;
        CloudLastServerTime = null;
    }

    /// <summary>Starts using this web server; a Firestore connection is dropped.</summary>
    public void ConnectWeb(string serverUrl, string token)
    {
        DisconnectCloud();
        WebServerUrl = serverUrl;
        WebToken = token;
        WebLastServerTime = WebSettingsSyncedAt = null;
    }

    public void DisconnectWeb()
    {
        WebServerUrl = WebToken = "";
        WebLastServerTime = WebSettingsSyncedAt = null;
    }

    public void DisconnectCloud()
    {
        CloudProjectId = CloudQueueKey = "";
        CloudLastServerTime = null;
    }
}
