using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Queueify.Models;

namespace Queueify.Services;

/// <summary>
/// Keeps the queue in step through a Queueify web server (ADR-0010): one POST to /api/sync sends this PC's
/// dirty albums and changed shared settings and brings back what changed on the server since the last
/// answer. Newest <c>UpdatedAt</c> wins per album, as with <see cref="CloudSync"/>; the server's clock only
/// orders its own answers, so PC clocks don't matter for what to download.
///
/// The HTTP client should not follow redirects (the app's own doesn't); a redirect, or an answer from
/// another host, is refused so the token can't be handed to somebody else. Errors never carry the
/// token, the address or the server's text.
/// </summary>
public sealed class ServerSync(HttpClient http, int maxBatch = ServerSync.MaxBatch) : ISyncProvider
{
    /// <summary>How long one request may take.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>Most albums in one request (ADR-0010); a bigger queue goes up in several rounds.</summary>
    public const int MaxBatch = 1000;

    /// <summary>Longest text the server accepts per field; longer text is shortened on upload.</summary>
    public const int MaxTextLength = CloudSync.MaxTextLength;

    private const int MaxExtraRounds = 20;
    private const long MaxAnswerBytes = 64L * 1024 * 1024;

    public async Task<SyncResult> SyncAsync(BacklogStore store, AppSettings s, CancellationToken ct)
    {
        if (!SyncConnect.TryParse(SyncConnect.Format(s.WebServerUrl, s.WebToken), out var baseUrl, out var token))
            throw new SyncException(Loc.T("webSync.err.not_configured"));
        var endpoint = new Uri(baseUrl + "/api/sync");

        var dirty = store.LoadDirty();
        var since = s.WebLastServerTime;
        bool firstConnection = since == null;
        var settingsToSend = SettingsToSend(s);
        int sent = 0, downloaded = 0, rounds = 0;
        bool settingsChanged = false, serverHadSettings = false;
        int maxRounds = MaxExtraRounds + dirty.Count / maxBatch;

        while (true)
        {
            if (++rounds > maxRounds) throw new SyncException(Loc.T("webSync.err.no_end"));

            var batch = dirty.Skip(sent).Take(maxBatch).ToList();
            var answer = await PostAsync(endpoint, token,
                new SyncRequest(since, batch.Select(ToWire).ToList(), settingsToSend), ct);

            // The server has the batch now. Rows it kept because its own were newer come back below.
            foreach (var item in batch) store.MarkUploaded(item);
            sent += batch.Count;
            if (settingsToSend != null)
            {
                s.WebSettingsSyncedAt = settingsToSend.UpdatedAt;
                settingsToSend = null;
            }

            foreach (var wire in answer.Albums)
            {
                if (string.IsNullOrEmpty(wire.Uid)) continue;
                var remote = ToItem(wire);
                var local = store.FindByUid(remote.Uid);
                if (local == null && remote.Deleted) continue;                       // deleted before this PC saw it
                if (local != null && local.UpdatedAt >= remote.UpdatedAt) continue;  // same or newer here
                store.ApplyRemote(remote);
                downloaded++;
            }

            if (answer.Settings is { } shared)
            {
                serverHadSettings = true;
                if (shared.UpdatedAt > (s.SharedSettingsUpdatedAt ?? DateTime.MinValue))
                {
                    TakeSettings(s, shared);
                    settingsChanged = true;
                }
            }

            since = answer.ServerTime;
            s.WebLastServerTime = since;

            bool more = sent < dirty.Count || answer.Albums.Count > 0;
            if (!more && firstConnection && !serverHadSettings && s.SharedSettingsUpdatedAt == null
                && (s.SpotifyConfigured || s.TidalConfigured))
            {
                // A new server with no keys yet: this PC's keys become the shared ones.
                s.SharedSettingsUpdatedAt = BacklogStore.Now();
                settingsToSend = SettingsToSend(s);
                more = true;
                firstConnection = false;
            }
            if (!more) break;
        }

        return new SyncResult(downloaded, dirty.Count, settingsChanged);
    }

    private async Task<SyncResponse> PostAsync(Uri endpoint, string token, SyncRequest body, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(body, SyncJson.Default.SyncRequest), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var res = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            if ((int)res.StatusCode is >= 300 and < 400
                || (res.RequestMessage?.RequestUri is { } final && !SameOrigin(final, endpoint)))
                throw new SyncException(Loc.T("webSync.err.redirect"));
            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new SyncException(Loc.T("webSync.err.unauthorized"));
            if (!res.IsSuccessStatusCode)
                throw new SyncException(Loc.F("webSync.err.server", (int)res.StatusCode));

            var text = await ReadBoundedAsync(res, timeout.Token);
            return JsonSerializer.Deserialize(text, SyncJson.Default.SyncResponse) is { Albums: not null } answer
                ? answer
                : throw new SyncException(Loc.T("webSync.err.bad_reply"));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SyncException(Loc.T("webSync.err.timeout"));
        }
        catch (HttpRequestException)
        {
            // Not kept as the inner exception: its message can name the address.
            throw new SyncException(Loc.T("webSync.err.offline"));
        }
        catch (JsonException)
        {
            throw new SyncException(Loc.T("webSync.err.bad_reply"));
        }
    }

    private static bool SameOrigin(Uri a, Uri b) =>
        a.Scheme == b.Scheme && a.Host == b.Host && a.Port == b.Port;

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.Content.Headers.ContentLength > MaxAnswerBytes) throw new SyncException(Loc.T("webSync.err.bad_reply"));
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + n > MaxAnswerBytes) throw new SyncException(Loc.T("webSync.err.bad_reply"));
            buffer.Write(chunk, 0, n);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    // ───────────── Mapping ─────────────

    private static SyncSettings? SettingsToSend(AppSettings s) =>
        s.SharedSettingsUpdatedAt is DateTime stamp && stamp != s.WebSettingsSyncedAt
            ? new SyncSettings(Cut(s.SpotifyClientId)!, Cut(s.SpotifyClientSecret)!, Cut(s.TidalClientId)!, Cut(s.TidalClientSecret)!,
                Cut(s.PreferredService)!, Cut(s.CountryCode)!, Utc(stamp))
            : null;

    private static void TakeSettings(AppSettings s, SyncSettings shared)
    {
        s.SpotifyClientId = shared.SpotifyClientId ?? "";
        s.SpotifyClientSecret = shared.SpotifyClientSecret ?? "";
        s.TidalClientId = shared.TidalClientId ?? "";
        s.TidalClientSecret = shared.TidalClientSecret ?? "";
        if (!string.IsNullOrWhiteSpace(shared.PreferredService)) s.PreferredService = shared.PreferredService;
        if (!string.IsNullOrWhiteSpace(shared.CountryCode)) s.CountryCode = shared.CountryCode;
        s.SharedSettingsUpdatedAt = s.WebSettingsSyncedAt = Utc(shared.UpdatedAt);
    }

    private static SyncAlbum ToWire(BacklogItem i) => new(
        i.Uid, Cut(i.Artist)!, Cut(i.Album)!, i.Year, Cut(i.Source), Cut(i.CoverUrl), Cut(i.SpotifyId), Cut(i.TidalId),
        i.AddedAt.ToUniversalTime(), i.ListenedAt?.ToUniversalTime(), i.Rating, Utc(i.UpdatedAt), i.Deleted);

    private static BacklogItem ToItem(SyncAlbum a) => new()
    {
        Uid = a.Uid,
        Artist = a.Artist ?? "",
        Album = a.Album ?? "",
        Year = a.Year,
        Source = a.Source,
        CoverUrl = a.CoverUrl,
        SpotifyId = a.SpotifyId,
        TidalId = a.TidalId,
        AddedAt = Utc(a.AddedAt).ToLocalTime(),
        ListenedAt = a.ListenedAt is DateTime l ? Utc(l).ToLocalTime() : null,
        Rating = double.IsNaN(a.Rating) || a.Rating < 0 ? -1 : Math.Min(a.Rating, 5),
        UpdatedAt = Utc(a.UpdatedAt),
        Deleted = a.Deleted,
    };

    private static string? Cut(string? text) =>
        text is { Length: > MaxTextLength } ? text[..MaxTextLength] : text;

    /// <summary>A time with no zone is taken as UTC, the zone every time on the wire is in.</summary>
    private static DateTime Utc(DateTime t) =>
        t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : t.ToUniversalTime();
}
