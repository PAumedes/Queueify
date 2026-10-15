using Queueify.Models;
using Queueify.Services;
using Queueify.Web.Storage;

namespace Queueify.Web.Services;

/// <summary>What the Settings page may know: whether a secret is set, never the secret.</summary>
public sealed record SettingsSnapshot(string SpotifyClientId, bool SpotifyConfigured, string TidalClientId, bool TidalConfigured, string PreferredService, string CountryCode);

/// <summary>What the form posts back. An empty secret means "keep the saved one".</summary>
public sealed record SettingsEdit(string SpotifyClientId, string SpotifyClientSecret, string TidalClientId, string TidalClientSecret, string PreferredService, string CountryCode)
{
    public static SettingsEdit From(SettingsSnapshot s) => new(s.SpotifyClientId, "", s.TidalClientId, "", s.PreferredService, s.CountryCode);
}

public enum KeyTestOutcome { Ok, Missing, Rejected, Failed, Unexpected }

/// <param name="Message">The catalog's own text for <see cref="KeyTestOutcome.Rejected"/> and <see cref="KeyTestOutcome.Failed"/>.</param>
public sealed record KeyTestResult(KeyTestOutcome Outcome, string? Message = null);

/// <summary>Reads and writes a user's music settings with the same edit rules as the desktop dialog.</summary>
public sealed class SettingsService(UserStores stores, Func<HttpClient> http)
{
    private const int MaxKeyLength = 300;

    public SettingsSnapshot Snapshot(string userId)
    {
        var s = stores.LoadSettings(userId).ForSettingsPage();
        return new(s.SpotifyClientId, s.SpotifyConfigured, s.TidalClientId, s.TidalConfigured, s.PreferredService, s.CountryCode);
    }

    /// <summary>
    /// Applies only what differs from <paramref name="opened"/> (<see cref="AppSettings.ApplyEdits"/>), on top of
    /// what is stored now. True when something shared changed.
    /// </summary>
    public bool Save(string userId, SettingsSnapshot opened, SettingsEdit edit)
    {
        var edited = new AppSettings
        {
            SpotifyClientId = Clean(edit.SpotifyClientId),
            SpotifyClientSecret = Clean(edit.SpotifyClientSecret),
            TidalClientId = Clean(edit.TidalClientId),
            TidalClientSecret = Clean(edit.TidalClientSecret),
            PreferredService = edit.PreferredService,
            CountryCode = edit.CountryCode,
        };
        if (edited.PreferredService is not (nameof(MusicService.Tidal) or nameof(MusicService.Spotify)))
            throw new ArgumentException("Not an offered service.", nameof(edit));
        if (!Countries.Codes.Contains(edited.CountryCode) && edited.CountryCode != opened.CountryCode)
            throw new ArgumentException("Not an offered country.", nameof(edit));

        // The secrets were never sent to the page, so the page's starting point has them empty.
        var start = new AppSettings
        {
            SpotifyClientId = opened.SpotifyClientId,
            TidalClientId = opened.TidalClientId,
            PreferredService = opened.PreferredService,
            CountryCode = opened.CountryCode,
        };

        var current = stores.LoadSettings(userId);
        if (!current.ApplyEdits(start, edited)) return false;
        current.SharedSettingsUpdatedAt = BacklogStore.Now();
        stores.SaveSettings(userId, current);
        return true;
    }

    public void RemoveKeys(string userId, MusicService service)
    {
        var current = stores.LoadSettings(userId);
        if (service == MusicService.Spotify) (current.SpotifyClientId, current.SpotifyClientSecret) = ("", "");
        else (current.TidalClientId, current.TidalClientSecret) = ("", "");
        current.SharedSettingsUpdatedAt = BacklogStore.Now();
        stores.SaveSettings(userId, current);
    }

    /// <summary>Searches the catalog once with the saved keys, so the user learns now whether they work.</summary>
    public async Task<KeyTestResult> TestAsync(string userId, MusicService service, CancellationToken ct)
    {
        var settings = stores.LoadSettings(userId);
        if (!(service == MusicService.Spotify ? settings.SpotifyConfigured : settings.TidalConfigured))
            return new(KeyTestOutcome.Missing);

        try
        {
            using var client = http();
            _ = service == MusicService.Spotify
                ? await new SpotifyCatalog(client, () => settings).SearchAsync("Abbey Road", ct)
                : await new TidalCatalog(client, () => settings).SearchAsync("Abbey Road", ct);
            return new(KeyTestOutcome.Ok);
        }
        catch (CatalogException ex)
        {
            return new(ex.KeysRejected ? KeyTestOutcome.Rejected : KeyTestOutcome.Failed, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new(KeyTestOutcome.Unexpected);
        }
    }

    private static string Clean(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= MaxKeyLength ? trimmed : throw new ArgumentException("Too long to be a key.", nameof(value));
    }
}
