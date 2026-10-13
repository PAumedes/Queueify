using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Queueify.Models;

namespace Queueify.Services;

/// <summary>
/// Tidal Open API v2 (JSON:API) album search using the Client Credentials flow.
/// Search returns album IDs only, so a second request fetches titles, artists and cover art.
/// </summary>
public sealed class TidalCatalog(HttpClient http, Func<AppSettings> settings)
{
    private const string Api = "https://openapi.tidal.com/v2";

    private string? _token;
    /// <summary>The keys the cached token was issued for: a changed ID or secret needs a new token.</summary>
    private string? _tokenKeys;
    private DateTime _tokenExpires;

    public async Task<List<AlbumMatch>> SearchAsync(string q, CancellationToken ct)
    {
        var s = settings();
        var country = Uri.EscapeDataString(s.CountryCode);
        var query = Uri.EscapeDataString(q.Trim());

        using var search = await GetJsonAsync(s, $"{Api}/searchResults?filter%5Bquery%5D={query}&countryCode={country}&includeLinkage=albums", ct);
        var ids = AlbumIds(search.RootElement).Take(10).ToList();
        if (ids.Count == 0) return [];

        var filter = string.Join("&", ids.Select(id => $"filter%5Bid%5D={Uri.EscapeDataString(id)}"));
        using var albums = await GetJsonAsync(s, $"{Api}/albums?countryCode={country}&include=artists,coverArt&{filter}", ct);
        var root = albums.RootElement;

        var artistNames = new Dictionary<string, string>();
        var artworkUrls = new Dictionary<string, string>();
        if (root.TryGetProperty("included", out var included))
        {
            foreach (var inc in included.EnumerateArray())
            {
                var id = inc.GetProperty("id").GetString()!;
                var type = inc.GetProperty("type").GetString();
                if (!inc.TryGetProperty("attributes", out var attrs)) continue;
                if (type == "artists" && attrs.TryGetProperty("name", out var name))
                    artistNames[id] = name.GetString() ?? "";
                else if (type == "artworks" && PickArtwork(attrs) is string url)
                    artworkUrls[id] = url;
            }
        }

        var byId = new Dictionary<string, AlbumMatch>();
        foreach (var a in root.GetProperty("data").EnumerateArray())
        {
            var id = a.GetProperty("id").GetString()!;
            var attrs = a.GetProperty("attributes");
            var rels = a.TryGetProperty("relationships", out var r) ? r : default;

            var title = attrs.GetProperty("title").GetString() ?? "";
            if (attrs.TryGetProperty("version", out var v) && v.GetString() is { Length: > 0 } version)
                title += $" ({version})";

            byId[id] = new AlbumMatch
            {
                Service = MusicService.Tidal,
                Id = id,
                Title = title,
                Artists = LinkedIds(rels, "artists").Select(x => artistNames.GetValueOrDefault(x, "")).Where(n => n.Length > 0).ToList(),
                Year = SpotifyCatalog.ParseYear(attrs.TryGetProperty("releaseDate", out var rd) ? rd.GetString() : null),
                AlbumType = attrs.TryGetProperty("albumType", out var t) ? t.GetString() : null,
                CoverUrl = LinkedIds(rels, "coverArt").Select(x => artworkUrls.GetValueOrDefault(x)).FirstOrDefault(u => u != null),
            };
        }

        // Keep Tidal's relevance order; the matcher re-sorts by score afterwards anyway.
        return ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }

    private static IEnumerable<string> AlbumIds(JsonElement root)
    {
        var data = root.GetProperty("data");
        var result = data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().FirstOrDefault() : data;
        return LinkedIds(result, "albums");
    }

    private static IEnumerable<string> LinkedIds(JsonElement resource, string relationship)
    {
        if (resource.ValueKind != JsonValueKind.Object) return [];
        if (resource.TryGetProperty("relationships", out var rels)) resource = rels;
        if (!resource.TryGetProperty(relationship, out var rel) || !rel.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
            return [];
        return data.EnumerateArray().Select(d => d.GetProperty("id").GetString()!).ToList();
    }

    private static string? PickArtwork(JsonElement attrs)
    {
        if (!attrs.TryGetProperty("files", out var files)) return null;
        string? best = null;
        int bestDiff = int.MaxValue;
        foreach (var f in files.EnumerateArray())
        {
            int w = f.TryGetProperty("meta", out var meta) && meta.TryGetProperty("width", out var wEl) ? wEl.GetInt32() : 320;
            int diff = Math.Abs(w - 320);
            if (diff < bestDiff) { bestDiff = diff; best = f.GetProperty("href").GetString(); }
        }
        return best;
    }

    private async Task<JsonDocument> GetJsonAsync(AppSettings s, string url, CancellationToken ct)
    {
        var token = await GetTokenAsync(s, ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.api+json"));

        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            if (res.StatusCode == HttpStatusCode.Unauthorized) _token = null;
            throw new CatalogException(res.StatusCode switch
            {
                HttpStatusCode.TooManyRequests => "Tidal is rate limiting requests. Wait a few seconds and retry.",
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    "Tidal refused the request. Check the Client ID / Client Secret in Settings.",
                _ => $"Tidal error {(int)res.StatusCode}: {SpotifyCatalog.Truncate(body)}",
            });
        }
        return JsonDocument.Parse(body);
    }

    private static string KeysOf(AppSettings s) => $"{s.TidalClientId.Trim()}:{s.TidalClientSecret.Trim()}";

    private async Task<string> GetTokenAsync(AppSettings s, CancellationToken ct)
    {
        if (_token != null && _tokenKeys == KeysOf(s) && DateTime.UtcNow < _tokenExpires)
            return _token;

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://auth.tidal.com/v1/oauth2/token")
        {
            Content = new FormUrlEncodedContent([new("grant_type", "client_credentials")]),
        };
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{s.TidalClientId.Trim()}:{s.TidalClientSecret.Trim()}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
            throw new CatalogException("Tidal rejected the Client ID / Client Secret. Check them in Settings.");

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        _token = doc.RootElement.GetProperty("access_token").GetString();
        _tokenKeys = KeysOf(s);
        _tokenExpires = DateTime.UtcNow.AddSeconds(doc.RootElement.GetProperty("expires_in").GetInt32() - 60);
        return _token!;
    }
}
