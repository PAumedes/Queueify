using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Queueify.Models;
using Queueify.Services;
using Queueify.Web.Storage;

namespace Queueify.Web.Sync;

/// <summary>The user's queue would hold more rows than the server allows.</summary>
public sealed class SyncQuotaException : Exception;

/// <summary>
/// The server side of <c>POST /api/sync</c> (ADR-0010) on one user's queue. The queue is the same SQLite file
/// <see cref="BacklogStore"/> writes, plus a <c>server_time</c> column and a <c>sync_meta</c> table that desktop
/// databases never have. Rows changed through <see cref="BacklogStore"/> by the web pages are flagged dirty;
/// each sync stamps them, so they reach the PCs without the pages knowing about sync.
/// </summary>
public sealed partial class SyncEngine(UserStores stores, TimeProvider clock, int maxRowsPerUser = SyncEngine.DefaultMaxRowsPerUser)
{
    public const int DefaultMaxRowsPerUser = 50_000;
    public const int MaxAlbums = 1000;
    public const int MaxText = 500;
    public static readonly TimeSpan TombstoneLifetime = TimeSpan.FromDays(90);
    private static readonly TimeSpan MaxClockAhead = TimeSpan.FromMinutes(10);

    private const string Columns =
        "uid, artist, album, year, source, cover_url, spotify_id, tidal_id, added_at, listened_at, rating, updated_at, deleted, server_time";

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<string, bool> _prepared = new();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex UidPattern();

    /// <summary>The answer to a sync, or null when the request is malformed (the endpoint answers 400). A caller that gave up while waiting for its turn costs nothing.</summary>
    public async Task<SyncResponse?> RunAsync(string userId, SyncRequest request, CancellationToken ct = default)
    {
        var clean = Clean(request);
        if (clean == null) return null;

        var gate = GateOf(userId);
        await gate.WaitAsync(ct);
        try { return await Task.Run(() => Apply(userId, clean), CancellationToken.None); }
        finally { gate.Release(); }
    }

    private SemaphoreSlim GateOf(string userId) => _gates.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Removes everything stored for the user, once a running sync for them is finished, and forgets that their file was
    /// prepared: the pages create a plain desktop-schema file again on the next visit, and the next sync must add the
    /// server columns to that one.
    /// </summary>
    public async Task DeleteUserDataAsync(string userId, CancellationToken ct = default)
    {
        var gate = GateOf(userId);
        await gate.WaitAsync(ct);
        try
        {
            _prepared.TryRemove(stores.QueuePathOf(userId), out _);
            stores.Delete(userId);
        }
        finally { gate.Release(); }
    }

    private sealed record Cleaned(long SinceTicks, List<SyncAlbum> Albums, SyncSettings? Settings);

    private Cleaned? Clean(SyncRequest request)
    {
        var albums = request.Albums ?? [];
        if (albums.Count > MaxAlbums) return null;
        var latest = clock.GetUtcNow().UtcDateTime + MaxClockAhead;

        var clean = new List<SyncAlbum>(albums.Count);
        foreach (var a in albums)
        {
            if (a == null || a.Uid == null || !UidPattern().IsMatch(a.Uid)) return null;
            clean.Add(new SyncAlbum(
                a.Uid, Cut(a.Artist), Cut(a.Album), a.Year is >= 1000 and <= 3000 ? a.Year : null,
                CutOrNull(a.Source), CutOrNull(a.CoverUrl), CutOrNull(a.SpotifyId), CutOrNull(a.TidalId),
                Utc(a.AddedAt), a.ListenedAt is DateTime l ? Utc(l) : null, Rating(a.Rating),
                Min(Utc(a.UpdatedAt), latest), a.Deleted));
        }

        SyncSettings? settings = null;
        if (request.Settings is { } s)
        {
            var shared = new AppSettings
            {
                SpotifyClientId = Cut(s.SpotifyClientId), SpotifyClientSecret = Cut(s.SpotifyClientSecret),
                TidalClientId = Cut(s.TidalClientId), TidalClientSecret = Cut(s.TidalClientSecret),
                PreferredService = Cut(s.PreferredService), CountryCode = Cut(s.CountryCode),
            };
            shared.Normalize();
            settings = new SyncSettings(shared.SpotifyClientId, shared.SpotifyClientSecret, shared.TidalClientId,
                shared.TidalClientSecret, shared.PreferredService, shared.CountryCode, Min(Utc(s.UpdatedAt), latest));
        }

        return new Cleaned(request.Since is DateTime since ? Utc(since).Ticks : 0, clean, settings);
    }

    private SyncResponse Apply(string userId, Cleaned request)
    {
        var path = stores.QueuePathOf(userId);
        Prepare(path);
        using var db = Open(path);
        using var tx = db.BeginTransaction();

        // Takes the write lock before anything is read, so two writers can't deadlock on an upgrade.
        Execute(db, "INSERT OR IGNORE INTO sync_meta (key, value) VALUES ('last_stamp', 0)");
        long last = Meta(db, "last_stamp");
        long nowTicks = clock.GetUtcNow().UtcDateTime.Ticks / 10 * 10;

        // A deletion is kept so the other PCs delete the album too; after this long every PC that syncs has had it.
        // A PC that stayed offline longer may keep an album that was deleted elsewhere.
        var cutoff = clock.GetUtcNow().UtcDateTime - TombstoneLifetime;
        Execute(db, "DELETE FROM items WHERE deleted = 1 AND dirty = 0 AND server_time < $t AND updated_at < $u",
            ("$t", cutoff.Ticks), ("$u", Stamp(cutoff)));
        long Next() => last = Math.Max(last + 10, nowTicks);

        // Changes made through BacklogStore (the web pages) get their stamp now.
        foreach (var uid in Strings(db, "SELECT uid FROM items WHERE dirty = 1"))
            Execute(db, "UPDATE items SET server_time = $t, dirty = 0 WHERE uid = $uid AND dirty = 1", ("$t", Next()), ("$uid", uid));

        var settings = stores.LoadSettings(userId);
        long settingsTicks = settings.SharedSettingsUpdatedAt is DateTime su ? Utc(su).Ticks : 0;
        if (settingsTicks != 0 && settingsTicks != Meta(db, "settings_seen"))
        {
            SetMeta(db, "settings_stamp", Next());
            SetMeta(db, "settings_seen", settingsTicks);
        }

        // Newest wins; on a tie the server keeps its row. A row the server refuses is stamped again, so it
        // goes back to the sender in this very response. (On an exact tie the PC keeps its own row, because
        // BacklogStore.ApplyRemote only takes strictly newer ones; microsecond ties are rare.)
        var accepted = new HashSet<string>();
        foreach (var incoming in request.Albums)
        {
            var existing = Get(db, incoming.Uid);
            if (existing == null || incoming.UpdatedAt > existing.UpdatedAt)
            {
                Upsert(db, incoming, Next());
                accepted.Add(incoming.Uid);
            }
            else if (existing != incoming)
                Execute(db, "UPDATE items SET server_time = $t WHERE uid = $uid", ("$t", Next()), ("$uid", incoming.Uid));
        }

        // Disposing the transaction without a commit undoes the whole request.
        using (var count = db.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM items";
            if ((long)count.ExecuteScalar()! > maxRowsPerUser) throw new SyncQuotaException();
        }

        bool settingsAccepted = false;
        if (request.Settings is { } sent)
        {
            if (sent.UpdatedAt.Ticks > settingsTicks)
            {
                settings.CopySharedFrom(ToSettings(sent));
                settings.SharedSettingsUpdatedAt = sent.UpdatedAt;
                stores.SaveSettings(userId, settings);
                SetMeta(db, "settings_stamp", Next());
                SetMeta(db, "settings_seen", sent.UpdatedAt.Ticks);
                settingsAccepted = true;
            }
            else if (ToWire(settings) != sent)
                SetMeta(db, "settings_stamp", Next());
        }

        var albums = new List<SyncAlbum>();
        bool truncated = false;
        long lastRowStamp = 0;
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = $"SELECT {Columns} FROM items WHERE server_time > $since ORDER BY server_time";
            cmd.Parameters.AddWithValue("$since", request.SinceTicks);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var album = ReadAlbum(r);
                if (accepted.Contains(album.Uid)) continue;
                if (albums.Count == MaxAlbums) { truncated = true; break; }
                albums.Add(album);
                lastRowStamp = r.GetInt64(13);
            }
        }

        // A full page ends where it ends, and the client asks again from there; otherwise this sync is the new "now".
        long serverTime = truncated ? lastRowStamp : Next();
        long settingsStamp = Meta(db, "settings_stamp");
        SyncSettings? settingsOut = !settingsAccepted && settingsStamp > request.SinceTicks && settingsStamp <= serverTime
            ? ToWire(stores.LoadSettings(userId))
            : null;

        SetMeta(db, "last_stamp", last);
        tx.Commit();
        return new SyncResponse(new DateTime(serverTime, DateTimeKind.Utc), albums, settingsOut);
    }

    private static AppSettings ToSettings(SyncSettings s) => new()
    {
        SpotifyClientId = s.SpotifyClientId, SpotifyClientSecret = s.SpotifyClientSecret,
        TidalClientId = s.TidalClientId, TidalClientSecret = s.TidalClientSecret,
        PreferredService = s.PreferredService, CountryCode = s.CountryCode,
    };

    private static SyncSettings ToWire(AppSettings s) => new(s.SpotifyClientId, s.SpotifyClientSecret, s.TidalClientId,
        s.TidalClientSecret, s.PreferredService, s.CountryCode, Utc(s.SharedSettingsUpdatedAt ?? DateTime.MinValue));

    // ── Cleaning ──

    private static string Cut(string? text)
    {
        if (text == null) return "";
        if (text.Length <= MaxText) return text;
        return char.IsHighSurrogate(text[MaxText - 1]) ? text[..(MaxText - 1)] : text[..MaxText];
    }

    private static string? CutOrNull(string? text) => text == null ? null : Cut(text);

    private static double Rating(double rating) => double.IsNaN(rating) ? -1 : Math.Clamp(rating, -1, 5);

    private static DateTime Utc(DateTime d) => d.Kind switch
    {
        DateTimeKind.Utc => d,
        DateTimeKind.Local => d.ToUniversalTime(),
        _ => DateTime.SpecifyKind(d, DateTimeKind.Utc),
    };

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    // ── SQLite ──

    private static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        return db;
    }

    private void Prepare(string path)
    {
        if (_prepared.ContainsKey(path)) return;
        _ = new BacklogStore(path);   // creates the folder and the desktop schema
        using var db = Open(path);
        bool has = false;
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(items)";
            using var r = cmd.ExecuteReader();
            while (r.Read()) has |= r.GetString(1) == "server_time";
        }
        if (!has) Execute(db, "ALTER TABLE items ADD COLUMN server_time INTEGER NOT NULL DEFAULT 0");
        Execute(db, "CREATE INDEX IF NOT EXISTS items_server_time ON items(server_time)");
        Execute(db, "CREATE TABLE IF NOT EXISTS sync_meta (key TEXT PRIMARY KEY, value INTEGER NOT NULL)");
        _prepared[path] = true;
    }

    private static void Execute(SqliteConnection db, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    private static List<string> Strings(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    private static long Meta(SqliteConnection db, string key)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT value FROM sync_meta WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() is long v ? v : 0;
    }

    private static void SetMeta(SqliteConnection db, string key, long value) =>
        Execute(db, "INSERT INTO sync_meta (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            ("$key", key), ("$value", value));

    private static SyncAlbum? Get(SqliteConnection db, string uid)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM items WHERE uid = $uid";
        cmd.Parameters.AddWithValue("$uid", uid);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadAlbum(r) : null;
    }

    private static void Upsert(SqliteConnection db, SyncAlbum a, long stamp) =>
        Execute(db, """
            INSERT INTO items (uid, artist, album, year, source, cover_url, spotify_id, tidal_id, added_at, listened_at, rating, updated_at, dirty, deleted, server_time)
            VALUES ($uid, $artist, $album, $year, $source, $cover, $spotify, $tidal, $added, $listened, $rating, $updated, 0, $deleted, $t)
            ON CONFLICT(uid) DO UPDATE SET artist = excluded.artist, album = excluded.album, year = excluded.year,
                source = excluded.source, cover_url = excluded.cover_url, spotify_id = excluded.spotify_id,
                tidal_id = excluded.tidal_id, added_at = excluded.added_at, listened_at = excluded.listened_at,
                rating = excluded.rating, updated_at = excluded.updated_at, dirty = 0, deleted = excluded.deleted,
                server_time = excluded.server_time
            """,
            ("$uid", a.Uid), ("$artist", a.Artist), ("$album", a.Album), ("$year", (object?)a.Year ?? DBNull.Value),
            ("$source", (object?)a.Source ?? DBNull.Value), ("$cover", (object?)a.CoverUrl ?? DBNull.Value),
            ("$spotify", (object?)a.SpotifyId ?? DBNull.Value), ("$tidal", (object?)a.TidalId ?? DBNull.Value),
            ("$added", Stamp(a.AddedAt)), ("$listened", a.ListenedAt is DateTime l ? Stamp(l) : DBNull.Value),
            ("$rating", a.Rating), ("$updated", Stamp(a.UpdatedAt)), ("$deleted", a.Deleted ? 1 : 0), ("$t", stamp));

    // The same fixed-width UTC text BacklogStore writes, so desktop code could read the file.
    private static string Stamp(DateTime d) => d.ToString("O", CultureInfo.InvariantCulture);

    private static DateTime Parse(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static SyncAlbum ReadAlbum(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetInt32(3),
        r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
        r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
        Parse(r.GetString(8)), r.IsDBNull(9) ? null : Parse(r.GetString(9)), r.GetDouble(10),
        Parse(r.GetString(11)), r.GetInt64(12) != 0);
}
