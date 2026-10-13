using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Queueify.Models;

namespace Queueify.Services;

/// <summary>
/// Spotify Web API album search using the Client Credentials flow (no user login needed).
/// Development Mode caps search at 10 results, which is plenty for "artist + album".
/// </summary>
public sealed class SpotifyCatalog(HttpClient http, Func<AppSettings> settings)
{
    private string? _token;
    /// <summary>The keys the cached token was issued for: a changed ID or secret needs a new token.</summary>
    private string? _tokenKeys;
    private DateTime _tokenExpires;

    public async Task<List<AlbumMatch>> SearchAsync(string q, CancellationToken ct)
    {
        var s = settings();
        var token = await GetTokenAsync(s, ct);
        var url = $"https://api.spotify.com/v1/search?type=album&limit=10&market={Uri.EscapeDataString(s.CountryCode)}&q={Uri.EscapeDataString(q)}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await http.SendAsync(req, ct);
        await ThrowIfFailedAsync(res, ct);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var matches = new List<AlbumMatch>();
        foreach (var a in doc.RootElement.GetProperty("albums").GetProperty("items").EnumerateArray())
        {
            if (a.ValueKind != JsonValueKind.Object) continue;
            matches.Add(new AlbumMatch
            {
                Service = MusicService.Spotify,
                Id = a.GetProperty("id").GetString()!,
                Title = a.GetProperty("name").GetString() ?? "",
                Artists = a.GetProperty("artists").EnumerateArray()
                    .Select(x => x.GetProperty("name").GetString() ?? "").ToList(),
                Year = ParseYear(a.TryGetProperty("release_date", out var rd) ? rd.GetString() : null),
                AlbumType = a.TryGetProperty("album_type", out var t) ? t.GetString() : null,
                CoverUrl = PickImage(a),
            });
        }
        return matches;
    }

    private static string KeysOf(AppSettings s) => $"{s.SpotifyClientId.Trim()}:{s.SpotifyClientSecret.Trim()}";

    private async Task<string> GetTokenAsync(AppSettings s, CancellationToken ct)
    {
        if (_token != null && _tokenKeys == KeysOf(s) && DateTime.UtcNow < _tokenExpires)
            return _token;

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token")
        {
            Content = new FormUrlEncodedContent([new("grant_type", "client_credentials")]),
        };
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{s.SpotifyClientId.Trim()}:{s.SpotifyClientSecret.Trim()}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            throw new CatalogException("Spotify rejected the Client ID / Client Secret. Check them in Settings.");
        await ThrowIfFailedAsync(res, ct);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        _token = doc.RootElement.GetProperty("access_token").GetString();
        _tokenKeys = KeysOf(s);
        _tokenExpires = DateTime.UtcNow.AddSeconds(doc.RootElement.GetProperty("expires_in").GetInt32() - 60);
        return _token!;
    }

    private async Task ThrowIfFailedAsync(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;
        var body = await res.Content.ReadAsStringAsync(ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized) _token = null;

        throw new CatalogException(res.StatusCode switch
        {
            HttpStatusCode.TooManyRequests when body.Contains("QUOTA_EXCEEDED") =>
                "Spotify's Development Mode quota is used up for now. Try again later.",
            HttpStatusCode.TooManyRequests => "Spotify is rate limiting requests. Wait a few seconds and retry.",
            HttpStatusCode.Forbidden =>
                "Spotify refused the request. Development Mode apps need the owner account to have Premium.",
            _ => $"Spotify error {(int)res.StatusCode}: {Truncate(body)}",
        });
    }

    private static string? PickImage(JsonElement album)
    {
        if (!album.TryGetProperty("images", out var images)) return null;
        string? best = null;
        int bestDiff = int.MaxValue;
        foreach (var img in images.EnumerateArray())
        {
            int w = img.TryGetProperty("width", out var wEl) && wEl.ValueKind == JsonValueKind.Number ? wEl.GetInt32() : 300;
            int diff = Math.Abs(w - 300);
            if (diff < bestDiff) { bestDiff = diff; best = img.GetProperty("url").GetString(); }
        }
        return best;
    }

    internal static int? ParseYear(string? date) =>
        date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), out var y) ? y : null;

    internal static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "…";
}
