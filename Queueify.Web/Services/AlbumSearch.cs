using System.Collections.Concurrent;
using Queueify.Models;
using Queueify.Services;

namespace Queueify.Web.Services;

/// <summary>Searches one catalog for one user, with the keys that user saved. The seam tests replace.</summary>
public interface ICatalogs
{
    Task<List<AlbumMatch>> SearchAsync(MusicService service, string query, string userId, AppSettings settings, CancellationToken ct);
}

/// <summary>
/// The real catalogs. Each user keeps their own Spotify/Tidal objects, so a token is reused between their
/// searches but never between users, and the keys are only ever read here on the server.
/// </summary>
public sealed class HttpCatalogs(IHttpClientFactory http) : ICatalogs
{
    public const string ClientName = "catalogs";

    private sealed class Pair
    {
        public volatile AppSettings Settings = new();
        public SpotifyCatalog Spotify { get; }
        public TidalCatalog Tidal { get; }

        public Pair(HttpClient client)
        {
            Spotify = new(client, () => Settings);
            Tidal = new(client, () => Settings);
        }
    }

    private readonly ConcurrentDictionary<string, Pair> _users = new();

    public Task<List<AlbumMatch>> SearchAsync(MusicService service, string query, string userId, AppSettings settings, CancellationToken ct)
    {
        var pair = _users.GetOrAdd(userId, _ => new Pair(http.CreateClient(ClientName)));
        pair.Settings = settings;
        return service == MusicService.Spotify ? pair.Spotify.SearchAsync(query, ct) : pair.Tidal.SearchAsync(query, ct);
    }
}

/// <summary>What one search found: the merged rows, what went wrong per service, and whether the best row is a confident match.</summary>
public sealed record SearchOutcome(IReadOnlyList<AlbumResult> Results, IReadOnlyList<string> Errors, bool Confident, bool NeedsKeys = false);

/// <summary>The Add box's search: both services at once, then the year retry, mirroring the desktop app.</summary>
public sealed class AlbumSearch(ICatalogs catalogs)
{
    /// <summary>Null when <paramref name="ct"/> was cancelled because a newer search replaced this one.</summary>
    public async Task<SearchOutcome?> RunAsync(string raw, string userId, AppSettings settings, CancellationToken ct)
    {
        if (!settings.SpotifyConfigured && !settings.TidalConfigured)
            return new SearchOutcome([], [], false, NeedsKeys: true);

        var errors = new List<string>();
        var found = new List<AlbumMatch>();

        async Task AskBothAsync(string q)
        {
            var asks = new List<Task<List<AlbumMatch>>>();
            if (settings.SpotifyConfigured) asks.Add(SafeAsync(MusicService.Spotify, q));
            if (settings.TidalConfigured) asks.Add(SafeAsync(MusicService.Tidal, q));
            foreach (var matches in await Task.WhenAll(asks))
                found.AddRange(matches.Where(m => !found.Any(f => f.Service == m.Service && f.Id == m.Id)));
        }

        async Task<List<AlbumMatch>> SafeAsync(MusicService service, string q)
        {
            var name = service.ToString();
            try { return await catalogs.SearchAsync(service, q, userId, settings, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return []; }
            catch (OperationCanceledException) { Report(name, Loc.T("search.timeout")); }
            catch (CatalogException ex) { Report(name, ex.Message); }
            catch (HttpRequestException ex) { Report(name, Loc.F("search.network", ex.Message)); }
            catch (Exception ex) { Report(name, Loc.F("search.unexpected", ex.Message)); }
            return [];
        }

        void Report(string service, string message)
        {
            var line = $"{service}: {message}";
            if (!errors.Contains(line)) errors.Add(line);
        }

        List<AlbumResult> Combine() => Matcher.Combine(
            found.Where(m => m.Service == MusicService.Spotify), found.Where(m => m.Service == MusicService.Tidal), raw);

        await AskBothAsync(Matcher.SearchText(raw));
        if (ct.IsCancellationRequested) return null;
        var results = Combine();

        // "Prince 1999": the first search left the year out; the number may be the title, so try with it.
        if (Matcher.ShouldRetryWithYear(raw, results))
        {
            await AskBothAsync(raw);
            if (ct.IsCancellationRequested) return null;
            results = Combine();
        }

        return new SearchOutcome(results, errors, results.Count > 0 && results[0].Score >= Matcher.ConfidentScore);
    }
}
