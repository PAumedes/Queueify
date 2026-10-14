using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Queueify.Models;

namespace Queueify.Services;

public sealed record SyncResult(int Downloaded, int Uploaded, bool SettingsChanged);

/// <summary>
/// Keeps the queue in sync between PCs through the user's own Google Firebase project (Cloud
/// Firestore REST API, free Spark plan). Each queue lives under a long random key that works as
/// its password; the published security rules only allow access to paths that contain such a key.
///
/// A sync downloads only albums changed since the previous sync (by server timestamp, so PC clocks
/// don't matter for that), then uploads this PC's pending changes. When both sides changed the same
/// album, the most recent change wins.
/// </summary>
public sealed partial class CloudSync(HttpClient http) : ISyncProvider
{
    /// <summary>The Firestore security rules to publish (tools/firestore.rules, embedded at build time).</summary>
    public static string SecurityRules { get; } = LoadRules();

    private static string LoadRules()
    {
        using var stream = typeof(CloudSync).Assembly.GetManifestResourceStream("firestore.rules")
            ?? throw new InvalidOperationException("firestore.rules is missing from the build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Set QUEUEIFY_FIRESTORE_EMULATOR=localhost:8080 to test against the local Firestore emulator.</summary>
    private static readonly string Host =
        Environment.GetEnvironmentVariable("QUEUEIFY_FIRESTORE_EMULATOR") is { Length: > 0 } emulator
            ? $"http://{emulator}"
            : "https://firestore.googleapis.com";

    /// <summary>Longest text the security rules accept per field; longer text is shortened on upload.</summary>
    public const int MaxTextLength = 500;

    /// <summary>Re-check a few seconds before the last seen change, in case of near-simultaneous commits.</summary>
    private static readonly TimeSpan Overlap = TimeSpan.FromSeconds(5);

    public static string NewQueueKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();

    public static string SyncCode(AppSettings s) => $"queueify:{s.CloudProjectId}:{s.CloudQueueKey}";

    public static bool TryParseSyncCode(string code, out string projectId, out string queueKey)
    {
        var parts = code.Trim().Split(':');
        bool ok = parts.Length == 3 && parts[0] == "queueify" && ProjectIdPattern().IsMatch(parts[1])
                  && parts[2].Length >= 32 && parts[2].All(char.IsAsciiHexDigit);
        projectId = ok ? parts[1] : "";
        queueKey = ok ? parts[2] : "";
        return ok;
    }

    /// <summary>Accepts a bare project ID or a pasted Firebase console URL (…/project/&lt;id&gt;/…).</summary>
    public static string? NormalizeProjectId(string input)
    {
        var text = input.Trim();
        var fromUrl = ProjectInUrl().Match(text);
        if (fromUrl.Success) text = fromUrl.Groups[1].Value;
        text = text.ToLowerInvariant();
        return ProjectIdPattern().IsMatch(text) ? text : null;
    }

    public async Task<SyncResult> SyncAsync(BacklogStore store, AppSettings s, CancellationToken ct)
    {
        var docs = $"projects/{s.CloudProjectId}/databases/(default)/documents";
        var queue = $"{docs}/queues/{s.CloudQueueKey}";

        // 1. Shared settings (API keys, preferred app, country): newest side wins.
        bool settingsChanged = false;
        var remoteSettings = await GetDocumentAsync($"{queue}/config/settings", ct);
        var remoteSettingsAt = remoteSettings != null ? Time(remoteSettings, "updatedAt") : null;
        if (remoteSettings != null && remoteSettingsAt > (s.SharedSettingsUpdatedAt ?? DateTime.MinValue))
        {
            s.SpotifyClientId = Text(remoteSettings, "spotifyClientId") ?? "";
            s.SpotifyClientSecret = Text(remoteSettings, "spotifyClientSecret") ?? "";
            s.TidalClientId = Text(remoteSettings, "tidalClientId") ?? "";
            s.TidalClientSecret = Text(remoteSettings, "tidalClientSecret") ?? "";
            s.PreferredService = Text(remoteSettings, "preferredService") ?? s.PreferredService;
            s.CountryCode = Text(remoteSettings, "countryCode") ?? s.CountryCode;
            s.SharedSettingsUpdatedAt = remoteSettingsAt;
            settingsChanged = true;
        }
        // Upload when this PC has newer settings, or when the queue has none yet and this PC has keys
        // (e.g. it joined a queue that was created before any keys were entered).
        bool hasKeys = s.SpotifyConfigured || s.TidalConfigured;
        bool uploadSettings = s.SharedSettingsUpdatedAt is DateTime mine
            ? remoteSettingsAt == null || mine > remoteSettingsAt
            : remoteSettings == null && hasKeys;
        if (uploadSettings) s.SharedSettingsUpdatedAt ??= BacklogStore.Now();

        // 2. Download albums other PCs changed since the last sync.
        int downloaded = 0;
        var since = s.CloudLastServerTime ?? DateTime.UnixEpoch;
        var newest = since;
        foreach (var (fields, serverTime) in await QueryChangedSinceAsync(queue, since - Overlap, ct))
        {
            if (serverTime > newest) newest = serverTime;
            var remote = ToItem(fields);
            var local = store.FindByUid(remote.Uid);
            if (local == null && remote.Deleted) continue;          // deleted before this PC ever saw it
            if (local != null && local.UpdatedAt >= remote.UpdatedAt) continue; // same or newer here
            store.ApplyRemote(remote);
            downloaded++;
        }
        s.CloudLastServerTime = newest;

        // 3. Upload this PC's pending changes (deletions included).
        var dirty = store.LoadDirty();
        var writes = dirty.Select(i => Write($"{queue}/albums/{i.Uid}", AlbumFields(i))).ToList();
        if (uploadSettings) writes.Add(Write($"{queue}/config/settings", SettingsFields(s)));
        foreach (var chunk in writes.Chunk(400))
            await CommitAsync(docs, chunk, ct);
        foreach (var item in dirty)
            store.MarkUploaded(item);

        return new SyncResult(downloaded, dirty.Count, settingsChanged);
    }

    // ───────────── Firestore REST calls ─────────────

    private async Task<JsonObject?> GetDocumentAsync(string path, CancellationToken ct)
    {
        using var res = await http.GetAsync($"{Host}/v1/{path}", ct);
        var body = await res.Content.ReadAsStringAsync(ct);

        // A missing document is normal (nothing uploaded yet); a missing database is a setup problem.
        if (res.StatusCode == HttpStatusCode.NotFound && !body.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!res.IsSuccessStatusCode) throw new CatalogException(Describe(res.StatusCode, body));
        return JsonNode.Parse(body)?["fields"]?.AsObject() ?? [];
    }

    private async Task<List<(JsonObject Fields, DateTime ServerTime)>> QueryChangedSinceAsync(string queue, DateTime since, CancellationToken ct)
    {
        var query = new JsonObject
        {
            ["structuredQuery"] = new JsonObject
            {
                ["from"] = new JsonArray(new JsonObject { ["collectionId"] = "albums" }),
                ["where"] = new JsonObject
                {
                    ["fieldFilter"] = new JsonObject
                    {
                        ["field"] = new JsonObject { ["fieldPath"] = "serverTime" },
                        ["op"] = "GREATER_THAN",
                        ["value"] = TimeValue(since < DateTime.UnixEpoch ? DateTime.UnixEpoch : since),
                    },
                },
                ["orderBy"] = new JsonArray(new JsonObject
                {
                    ["field"] = new JsonObject { ["fieldPath"] = "serverTime" },
                    ["direction"] = "ASCENDING",
                }),
            },
        };

        using var res = await http.PostAsync($"{Host}/v1/{queue}:runQuery", Json(query), ct);
        var body = await ReadOrThrowAsync(res, ct);

        var changes = new List<(JsonObject, DateTime)>();
        foreach (var entry in JsonNode.Parse(body)?.AsArray() ?? [])
        {
            if (entry?["error"] is JsonNode error) throw new CatalogException(Describe(res.StatusCode, error.ToJsonString()));
            if (entry?["document"]?["fields"] is not JsonObject fields) continue;
            changes.Add((fields, Time(fields, "serverTime") ?? DateTime.UnixEpoch));
        }
        return changes;
    }

    private async Task CommitAsync(string docs, IEnumerable<JsonObject> writes, CancellationToken ct)
    {
        var body = new JsonObject { ["writes"] = new JsonArray(writes.Select(w => (JsonNode)w).ToArray()) };
        using var res = await http.PostAsync($"{Host}/v1/{docs}:commit", Json(body), ct);
        if (res.StatusCode == HttpStatusCode.Forbidden)
        {
            // Reading worked a moment ago, so the rules exist; most likely they are an older version.
            throw new CatalogException(
                Loc.T("firebase.write_refused"));
        }
        await ReadOrThrowAsync(res, ct);
    }

    private static JsonObject Write(string path, JsonObject fields) => new()
    {
        ["update"] = new JsonObject { ["name"] = path, ["fields"] = fields },
        ["updateTransforms"] = new JsonArray(new JsonObject
        {
            ["fieldPath"] = "serverTime",
            ["setToServerValue"] = "REQUEST_TIME",
        }),
    };

    private static StringContent Json(JsonNode node) => new(node.ToJsonString(), Encoding.UTF8, "application/json");

    private static async Task<string> ReadOrThrowAsync(HttpResponseMessage res, CancellationToken ct)
    {
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new CatalogException(Describe(res.StatusCode, body));
        return body;
    }

    private static string Describe(HttpStatusCode status, string body)
    {
        string message = "";
        try
        {
            var node = JsonNode.Parse(body);
            if (node is JsonArray array) node = array.FirstOrDefault();
            message = node?["error"]?["message"]?.GetValue<string>() ?? "";
        }
        catch (JsonException)
        {
            message = body.Length > 160 ? body[..160] : body;
        }

        return status switch
        {
            HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized =>
                Loc.T("firebase.refused"),
            HttpStatusCode.NotFound =>
                Loc.T("firebase.no_database"),
            HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable =>
                Loc.T("firebase.busy"),
            _ => Loc.F("firebase.error", (int)status, message),
        };
    }

    // ───────────── Field mapping ─────────────

    private static JsonObject AlbumFields(BacklogItem i) => new()
    {
        ["artist"] = TextValue(i.Artist),
        ["album"] = TextValue(i.Album),
        ["year"] = i.Year is int y ? new JsonObject { ["integerValue"] = y.ToString(CultureInfo.InvariantCulture) } : Null(),
        ["source"] = TextValue(i.Source),
        ["coverUrl"] = TextValue(i.CoverUrl),
        ["spotifyId"] = TextValue(i.SpotifyId),
        ["tidalId"] = TextValue(i.TidalId),
        ["addedAt"] = TimeValue(i.AddedAt.ToUniversalTime()),
        ["listenedAt"] = i.ListenedAt is DateTime l ? TimeValue(l.ToUniversalTime()) : Null(),
        ["rating"] = new JsonObject { ["doubleValue"] = i.Rating },
        ["updatedAt"] = TimeValue(i.UpdatedAt),
        ["deleted"] = new JsonObject { ["booleanValue"] = i.Deleted },
        ["uid"] = TextValue(i.Uid),
    };

    private static BacklogItem ToItem(JsonObject f) => new()
    {
        Uid = Text(f, "uid") ?? throw new CatalogException(Loc.T("firebase.no_id")),
        Artist = Text(f, "artist") ?? "",
        Album = Text(f, "album") ?? "",
        Year = f["year"]?["integerValue"]?.GetValue<string>() is string y
               && int.TryParse(y, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) ? year : null,
        Source = Text(f, "source"),
        CoverUrl = Text(f, "coverUrl"),
        SpotifyId = Text(f, "spotifyId"),
        TidalId = Text(f, "tidalId"),
        AddedAt = (Time(f, "addedAt") ?? DateTime.UtcNow).ToLocalTime(),
        ListenedAt = Time(f, "listenedAt")?.ToLocalTime(),
        Rating = Number(f, "rating") ?? -1,
        UpdatedAt = Time(f, "updatedAt") ?? DateTime.UnixEpoch,
        Deleted = f["deleted"]?["booleanValue"]?.GetValue<bool>() ?? false,
    };

    private static JsonObject SettingsFields(AppSettings s) => new()
    {
        ["spotifyClientId"] = TextValue(s.SpotifyClientId),
        ["spotifyClientSecret"] = TextValue(s.SpotifyClientSecret),
        ["tidalClientId"] = TextValue(s.TidalClientId),
        ["tidalClientSecret"] = TextValue(s.TidalClientSecret),
        ["preferredService"] = TextValue(s.PreferredService),
        ["countryCode"] = TextValue(s.CountryCode),
        ["updatedAt"] = TimeValue(s.SharedSettingsUpdatedAt ?? BacklogStore.Now()),
    };

    private static JsonObject Null() => new() { ["nullValue"] = null };

    private static JsonObject TextValue(string? value) =>
        value == null ? Null() : new JsonObject { ["stringValue"] = value.Length <= MaxTextLength ? value : value[..MaxTextLength] };

    private static JsonObject TimeValue(DateTime utc) => new()
    {
        ["timestampValue"] = utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture),
    };

    private static string? Text(JsonObject f, string name) => f[name]?["stringValue"]?.GetValue<string>();

    private static double? Number(JsonObject f, string name) =>
        f[name]?["doubleValue"]?.GetValue<double>()
        ?? (f[name]?["integerValue"]?.GetValue<string>() is string i ? double.Parse(i, CultureInfo.InvariantCulture) : null);

    private static DateTime? Time(JsonObject f, string name) =>
        f[name]?["timestampValue"]?.GetValue<string>() is string t
            ? DateTime.Parse(t, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
            : null;

    [GeneratedRegex(@"^[a-z][a-z0-9-]{4,29}$")]
    private static partial Regex ProjectIdPattern();

    [GeneratedRegex(@"/project/([A-Za-z0-9-]+)")]
    private static partial Regex ProjectInUrl();
}
