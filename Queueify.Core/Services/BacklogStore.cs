using System.Globalization;
using Microsoft.Data.Sqlite;
using Queueify.Models;

namespace Queueify.Services;

/// <summary>
/// SQLite persistence for the album queue. Every change is stamped and flagged so online sync can
/// upload it; deletions are kept as markers until uploaded so other PCs delete the album too.
/// </summary>
public sealed class BacklogStore
{
    private const string Columns =
        "id, uid, artist, album, year, source, cover_url, spotify_id, tidal_id, added_at, listened_at, rating, updated_at, dirty, deleted";

    private readonly string _connectionString;

    public BacklogStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();

        using var db = Open();
        Execute(db, """
            CREATE TABLE IF NOT EXISTS items (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                artist      TEXT NOT NULL,
                album       TEXT NOT NULL,
                year        INTEGER NULL,
                source      TEXT NULL,
                cover_url   TEXT NULL,
                spotify_id  TEXT NULL,
                tidal_id    TEXT NULL,
                added_at    TEXT NOT NULL,
                listened_at TEXT NULL,
                rating      REAL NOT NULL DEFAULT -1
            );
            """);

        // Databases from earlier versions don't have the sync columns yet.
        var existing = new HashSet<string>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(items)";
            using var r = cmd.ExecuteReader();
            while (r.Read()) existing.Add(r.GetString(1));
        }
        if (!existing.Contains("uid")) Execute(db, "ALTER TABLE items ADD COLUMN uid TEXT NULL");
        if (!existing.Contains("updated_at")) Execute(db, "ALTER TABLE items ADD COLUMN updated_at TEXT NULL");
        if (!existing.Contains("dirty")) Execute(db, "ALTER TABLE items ADD COLUMN dirty INTEGER NOT NULL DEFAULT 1");
        if (!existing.Contains("deleted")) Execute(db, "ALTER TABLE items ADD COLUMN deleted INTEGER NOT NULL DEFAULT 0");
        Execute(db, "UPDATE items SET uid = lower(hex(randomblob(16))) WHERE uid IS NULL");
        Execute(db, $"UPDATE items SET updated_at = '{Stamp(DateTime.UtcNow)}' WHERE updated_at IS NULL");
        Execute(db, "CREATE UNIQUE INDEX IF NOT EXISTS items_uid ON items(uid)");
    }

    /// <summary>The queue as shown in the app (without deleted albums).</summary>
    public List<BacklogItem> LoadAll() => Query($"SELECT {Columns} FROM items WHERE deleted = 0");

    /// <summary>Albums changed on this PC that still need uploading, deletions included.</summary>
    public List<BacklogItem> LoadDirty() => Query($"SELECT {Columns} FROM items WHERE dirty = 1");

    /// <summary>Queues every album for upload, e.g. when connecting to a (new) online queue.</summary>
    public void MarkAllDirty()
    {
        using var db = Open();
        Execute(db, "UPDATE items SET dirty = 1");
    }

    public BacklogItem? FindByUid(string uid) =>
        Query($"SELECT {Columns} FROM items WHERE uid = $uid", ("$uid", uid)).FirstOrDefault();

    public void Insert(BacklogItem item)
    {
        if (item.Uid.Length == 0) item.Uid = Guid.NewGuid().ToString("N");
        Touch(item);
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO items ({Columns[4..]})
            VALUES ($uid, $artist, $album, $year, $source, $cover, $spotify, $tidal, $added, $listened, $rating, $updated, $dirty, $deleted);
            SELECT last_insert_rowid();
            """;
        Bind(cmd, item);
        item.Id = (long)cmd.ExecuteScalar()!;
    }

    public void Update(BacklogItem item)
    {
        Touch(item);
        Save(item);
    }

    /// <summary>Removes the album from the queue; the deletion syncs to other PCs.</summary>
    public void Delete(BacklogItem item)
    {
        item.Deleted = true;
        Update(item);
    }

    /// <summary>Undoes <see cref="Delete"/>, even if the deletion was already uploaded.</summary>
    public void Restore(BacklogItem item)
    {
        item.Deleted = false;
        Touch(item);
        if (Save(item) == 0) Insert(item);
    }

    /// <summary>
    /// Applies a change downloaded from another PC: adds or overwrites the album by its shared id,
    /// or removes it if it was deleted there. A local version that is newer always wins; the check
    /// is part of the SQL, so an edit made while a sync runs in the background can't be lost.
    /// </summary>
    public void ApplyRemote(BacklogItem remote)
    {
        using var db = Open();
        if (remote.Deleted)
        {
            using var del = db.CreateCommand();
            del.CommandText = "DELETE FROM items WHERE uid = $uid AND updated_at < $updated";
            del.Parameters.AddWithValue("$uid", remote.Uid);
            del.Parameters.AddWithValue("$updated", Stamp(remote.UpdatedAt));
            del.ExecuteNonQuery();
            return;
        }

        remote.Dirty = false;
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO items ({Columns[4..]})
            VALUES ($uid, $artist, $album, $year, $source, $cover, $spotify, $tidal, $added, $listened, $rating, $updated, $dirty, $deleted)
            ON CONFLICT(uid) DO UPDATE SET artist = excluded.artist, album = excluded.album, year = excluded.year,
                source = excluded.source, cover_url = excluded.cover_url, spotify_id = excluded.spotify_id,
                tidal_id = excluded.tidal_id, added_at = excluded.added_at, listened_at = excluded.listened_at,
                rating = excluded.rating, updated_at = excluded.updated_at, dirty = 0, deleted = 0
            WHERE items.updated_at < excluded.updated_at
            """;
        Bind(cmd, remote);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Brings in albums from a backup in one transaction, keeping each album's own <c>UpdatedAt</c> (so "newest
    /// wins" still means the album's last edit, not the day of the import). A new album is added; one already here
    /// is overwritten only when the backup's copy is newer. An album deleted here is not brought back.
    /// Returns how many were added and how many were already here.
    /// </summary>
    public (int Added, int AlreadyHere) MergeBackup(IEnumerable<BacklogItem> items)
    {
        int added = 0, here = 0;
        using var db = Open();
        using var tx = db.BeginTransaction();
        foreach (var item in items)
        {
            if (item.Uid.Length == 0) item.Uid = Guid.NewGuid().ToString("N");
            item.Dirty = true;   // queued for the server's sync stamp, so connected PCs receive the imported albums
            item.Deleted = false;

            using var find = db.CreateCommand();
            find.Transaction = tx;
            find.CommandText = "SELECT deleted, updated_at FROM items WHERE uid = $uid";
            find.Parameters.AddWithValue("$uid", item.Uid);
            using (var r = find.ExecuteReader())
            {
                if (r.Read())
                {
                    here++;
                    if (r.GetInt64(0) != 0 || ParseDate(r.GetString(1)).ToUniversalTime() >= item.UpdatedAt.ToUniversalTime()) continue;
                    using var update = db.CreateCommand();
                    update.Transaction = tx;
                    update.CommandText = """
                        UPDATE items SET artist = $artist, album = $album, year = $year, source = $source, cover_url = $cover,
                            spotify_id = $spotify, tidal_id = $tidal, added_at = $added, listened_at = $listened, rating = $rating,
                            updated_at = $updated, dirty = $dirty, deleted = 0
                        WHERE uid = $uid
                        """;
                    Bind(update, item);
                    update.ExecuteNonQuery();
                    continue;
                }
            }

            using var insert = db.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = $"""
                INSERT INTO items ({Columns[4..]})
                VALUES ($uid, $artist, $album, $year, $source, $cover, $spotify, $tidal, $added, $listened, $rating, $updated, $dirty, $deleted)
                """;
            Bind(insert, item);
            insert.ExecuteNonQuery();
            added++;
        }
        tx.Commit();
        return (added, here);
    }

    /// <summary>
    /// Marks an uploaded change as done, unless the album changed again meanwhile. Uploaded
    /// deletions are removed for good.
    /// </summary>
    public void MarkUploaded(BacklogItem item)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE items SET dirty = 0 WHERE uid = $uid AND updated_at = $updated;
            DELETE FROM items WHERE uid = $uid AND deleted = 1 AND dirty = 0;
            """;
        cmd.Parameters.AddWithValue("$uid", item.Uid);
        cmd.Parameters.AddWithValue("$updated", Stamp(item.UpdatedAt));
        cmd.ExecuteNonQuery();
    }

    /// <summary>UTC "now" cut to whole microseconds, the precision Firestore stores, so timestamps compare exactly after a round trip.</summary>
    public static DateTime Now()
    {
        var ticks = DateTime.UtcNow.Ticks;
        return new DateTime(ticks - ticks % 10, DateTimeKind.Utc);
    }

    private static void Touch(BacklogItem item)
    {
        item.UpdatedAt = Now();
        item.Dirty = true;
    }

    private int Save(BacklogItem item)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE items SET artist = $artist, album = $album, year = $year, source = $source, cover_url = $cover,
                spotify_id = $spotify, tidal_id = $tidal, added_at = $added, listened_at = $listened, rating = $rating,
                updated_at = $updated, dirty = $dirty, deleted = $deleted
            WHERE uid = $uid
            """;
        Bind(cmd, item);
        return cmd.ExecuteNonQuery();
    }

    private List<BacklogItem> Query(string sql, params (string Name, object Value)[] parameters)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        using var r = cmd.ExecuteReader();

        var items = new List<BacklogItem>();
        while (r.Read())
        {
            items.Add(new BacklogItem
            {
                Id = r.GetInt64(0),
                Uid = r.GetString(1),
                Artist = r.GetString(2),
                Album = r.GetString(3),
                Year = r.IsDBNull(4) ? null : r.GetInt32(4),
                Source = r.IsDBNull(5) ? null : r.GetString(5),
                CoverUrl = r.IsDBNull(6) ? null : r.GetString(6),
                SpotifyId = r.IsDBNull(7) ? null : r.GetString(7),
                TidalId = r.IsDBNull(8) ? null : r.GetString(8),
                AddedAt = ParseDate(r.GetString(9)),
                ListenedAt = r.IsDBNull(10) ? null : ParseDate(r.GetString(10)),
                Rating = r.GetDouble(11),
                UpdatedAt = ParseDate(r.GetString(12)).ToUniversalTime(),
                Dirty = r.GetInt64(13) != 0,
                Deleted = r.GetInt64(14) != 0,
            });
        }
        return items;
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        return db;
    }

    private static void Execute(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void Bind(SqliteCommand cmd, BacklogItem item)
    {
        cmd.Parameters.AddWithValue("$uid", item.Uid);
        cmd.Parameters.AddWithValue("$artist", item.Artist);
        cmd.Parameters.AddWithValue("$album", item.Album);
        cmd.Parameters.AddWithValue("$year", (object?)item.Year ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$source", (object?)item.Source ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cover", (object?)item.CoverUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$spotify", (object?)item.SpotifyId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tidal", (object?)item.TidalId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$added", Stamp(item.AddedAt));
        cmd.Parameters.AddWithValue("$listened", item.ListenedAt is DateTime l ? Stamp(l) : DBNull.Value);
        cmd.Parameters.AddWithValue("$rating", item.Rating);
        cmd.Parameters.AddWithValue("$updated", Stamp(item.UpdatedAt));
        cmd.Parameters.AddWithValue("$dirty", item.Dirty ? 1 : 0);
        cmd.Parameters.AddWithValue("$deleted", item.Deleted ? 1 : 0);
    }

    /// <summary>
    /// Timestamps are stored as fixed-width UTC strings ("O" format), so SQL can compare them as text.
    /// AddedAt/ListenedAt keep the local offset; they are only displayed, never compared.
    /// </summary>
    private static string Stamp(DateTime d) => d.ToString("O", CultureInfo.InvariantCulture);

    private static DateTime ParseDate(string s) =>
        DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
