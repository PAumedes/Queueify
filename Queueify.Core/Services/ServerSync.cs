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

    /// <summary>Re-ask a few seconds before the last answer, in case the server committed two changes at nearly the same moment.</summary>
    public static readonly TimeSpan Overlap = TimeSpan.FromSeconds(5);

    /// <summary>Largest answer read (about 50,000 albums); a bigger one is not a Queueify server.</summary>
    public const long MaxAnswerBytes = 16L * 1024 * 1024;

    // A full queue (50,000 albums) arrives in 50 pages, so the limit that stops a server that never ends must be above that.
    private const int MaxExtraRounds = 60;
    private const int MaxUidLength = 64;
    private static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);

    /// <summary>The client for a web server: it never follows a redirect, so the token can't be handed to another host.</summary>
    public static HttpClient CreateClient()
    {
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Queueify/" + AppInfo.Version);
        return http;
    }

    public async Task<SyncResult> SyncAsync(BacklogStore store, AppSettings s, CancellationToken ct)
    {
        if (!SyncConnect.TryParse(SyncConnect.Format(s.WebServerUrl, s.WebToken), out var baseUrl, out var token))
            throw new SyncException(Loc.T("webSync.err.not_configured"));
        var endpoint = new Uri(baseUrl + "/api/sync");

        var now = BacklogStore.Now();
        var dirty = store.LoadDirty();
        // Only the first ask overlaps; later rounds start exactly at the last answer, so they can end.
        var since = s.WebLastServerTime is DateTime last && last > DateTime.UnixEpoch + Overlap ? last - Overlap : (DateTime?)null;
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

            // A missing or absurd time would be stored as the next position and break every later sync.
            if (answer.ServerTime <= DateTime.UnixEpoch || answer.ServerTime > now + FutureTolerance)
                throw new SyncException(Loc.T("webSync.err.bad_reply"));

            // The server has the batch now. Rows it kept because its own were newer come back below.
            foreach (var item in batch) store.MarkUploaded(item);
            sent += batch.Count;
            if (settingsToSend != null)
            {
                s.WebSettingsSyncedAt = settingsToSend.UpdatedAt;
                settingsToSend = null;
            }

            // One transaction per page. The store keeps the newer of the two versions of each album, and tells how
            // many changed here, so rows already known (the overlap, or ones just sent) don't count.
            var remotes = new List<BacklogItem>(answer.Albums.Count);
            foreach (var wire in answer.Albums)
            {
                if (wire.Uid is not { Length: > 0 and <= MaxUidLength }) continue;
                remotes.Add(ToItem(wire, now));
            }
            int applied = store.ApplyRemoteBatch(remotes);
            downloaded += applied;

            if (answer.Settings is { } shared)
            {
                serverHadSettings = true;
                if (Past(shared.UpdatedAt, now) > (s.SharedSettingsUpdatedAt ?? DateTime.MinValue))
                {
                    TakeSettings(s, shared, now);
                    settingsChanged = true;
                }
            }

            // The server answers at most MaxBatch albums and then says where it stopped, so a full page that moved the
            // position means "maybe more" even when nothing on it was new to this PC (deletions it never had, say).
            bool fullPage = answer.Albums.Count >= MaxBatch && answer.ServerTime > (since ?? DateTime.MinValue);
            since = answer.ServerTime;
            s.WebLastServerTime = since;

            // Rows already known (the overlap, or ones just sent) don't count, or an idle sync would never end.
            bool more = sent < dirty.Count || applied > 0 || fullPage;
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

            var bytes = await ReadBoundedAsync(res, timeout.Token);
            return JsonSerializer.Deserialize(bytes.Span, SyncJson.Default.SyncResponse) is { Albums: not null } answer
                   && !answer.Albums.Contains(null!)
                ? answer
                : throw new SyncException(Loc.T("webSync.err.bad_reply"));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SyncException(Loc.T("webSync.err.timeout"));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
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

    private static async Task<ReadOnlyMemory<byte>> ReadBoundedAsync(HttpResponseMessage res, CancellationToken ct)
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
        return buffer.GetBuffer().AsMemory(0, (int)buffer.Length);
    }

    // ───────────── Mapping ─────────────

    private static SyncSettings? SettingsToSend(AppSettings s) =>
        s.SharedSettingsUpdatedAt is DateTime stamp && stamp != s.WebSettingsSyncedAt
            ? new SyncSettings(Cut(s.SpotifyClientId)!, Cut(s.SpotifyClientSecret)!, Cut(s.TidalClientId)!, Cut(s.TidalClientSecret)!,
                Cut(s.PreferredService)!, Cut(s.CountryCode)!, Utc(stamp))
            : null;

    private static void TakeSettings(AppSettings s, SyncSettings shared, DateTime now)
    {
        s.SpotifyClientId = Cut(shared.SpotifyClientId) ?? "";
        s.SpotifyClientSecret = Cut(shared.SpotifyClientSecret) ?? "";
        s.TidalClientId = Cut(shared.TidalClientId) ?? "";
        s.TidalClientSecret = Cut(shared.TidalClientSecret) ?? "";
        if (!string.IsNullOrWhiteSpace(shared.PreferredService)) s.PreferredService = Cut(shared.PreferredService)!;
        if (!string.IsNullOrWhiteSpace(shared.CountryCode)) s.CountryCode = Cut(shared.CountryCode)!;
        s.SharedSettingsUpdatedAt = s.WebSettingsSyncedAt = Past(shared.UpdatedAt, now);
    }

    private static SyncAlbum ToWire(BacklogItem i) => new(
        i.Uid, Cut(i.Artist)!, Cut(i.Album)!, i.Year, Cut(i.Source), Cut(i.CoverUrl), Cut(i.SpotifyId), Cut(i.TidalId),
        i.AddedAt.ToUniversalTime(), i.ListenedAt?.ToUniversalTime(), i.Rating, Utc(i.UpdatedAt), i.Deleted);

    private static BacklogItem ToItem(SyncAlbum a, DateTime now) => new()
    {
        Uid = a.Uid,
        Artist = Cut(a.Artist) ?? "",
        Album = Cut(a.Album) ?? "",
        Year = AlbumYear.OrNull(a.Year),
        Source = Cut(a.Source),
        CoverUrl = Cut(a.CoverUrl),
        SpotifyId = PlainId(a.SpotifyId),
        TidalId = PlainId(a.TidalId),
        AddedAt = Utc(a.AddedAt).ToLocalTime(),
        ListenedAt = a.ListenedAt is DateTime l ? Utc(l).ToLocalTime() : null,
        Rating = double.IsNaN(a.Rating) || a.Rating < 0 ? -1 : Math.Min(a.Rating, 5),
        UpdatedAt = Past(a.UpdatedAt, now),
        Deleted = a.Deleted,
    };

    /// <summary>
    /// A change time more than a few minutes ahead is taken as now: a server (or a wrong clock) must not
    /// be able to make a row win every later comparison.
    /// </summary>
    private static DateTime Past(DateTime t, DateTime now)
    {
        var utc = Utc(t);
        return utc > now + FutureTolerance ? now : utc;   // one fixed "now" per sync, so the same row isn't "newer" every round
    }
    /// <summary>A service id is kept only when it is a plain token (it ends up in a link the OS opens), as the server checks uids.</summary>
    private static string? PlainId(string? id) => PlayLinks.IsPlainId(id) ? id : null;


    private static string? Cut(string? text) =>
        text == null ? null : CloudSync.CutText(text);

    /// <summary>A time with no zone is taken as UTC, the zone every time on the wire is in.</summary>
    private static DateTime Utc(DateTime t) =>
        t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : t.ToUniversalTime();
}
