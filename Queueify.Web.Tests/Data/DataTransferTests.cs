using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Queueify.Models;
using Queueify.Services;
using Queueify.Web.Services;
using Queueify.Web.Storage;

namespace Queueify.Web.Tests.Data;

public sealed class DataTransferTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qd-" + Guid.NewGuid().ToString("N"));
    private readonly UserStores _stores;
    private readonly DataTransfer _transfer;

    public DataTransferTests()
    {
        _stores = new UserStores(_root);
        _transfer = new DataTransfer(_stores);
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(_root, true); } catch (IOException) { } }

    private static BacklogItem Album(string artist, string album, string uid = "") =>
        new() { Artist = artist, Album = album, Uid = uid, Year = 1983, Source = "Rolling Stone 214", AddedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), SpotifyId = "sp1", TidalId = "td1" };

    /// <summary>A database as the desktop app writes it: made by BacklogStore itself.</summary>
    private byte[] DesktopDb(params BacklogItem[] items)
    {
        var path = Path.Combine(_root, "desktop-" + Guid.NewGuid().ToString("N") + ".db");
        var store = new BacklogStore(path);
        foreach (var item in items) store.Insert(item);
        SqliteConnection.ClearAllPools();
        return File.ReadAllBytes(path);
    }

    private byte[] RawDb(params string[] statements)
    {
        var path = Path.Combine(_root, "raw-" + Guid.NewGuid().ToString("N") + ".db");
        Directory.CreateDirectory(_root);
        using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            db.Open();
            foreach (var sql in statements)
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }
        SqliteConnection.ClearAllPools();
        return File.ReadAllBytes(path);
    }

    private ImportProblem ProblemOf(byte[] bytes) =>
        Assert.Throws<ImportException>(() => _transfer.Import("alice", bytes)).Problem;

    private static string ItemsTable(string columns = "id INTEGER PRIMARY KEY, uid TEXT, artist TEXT, album TEXT, year INTEGER, source TEXT, cover_url TEXT, spotify_id TEXT, tidal_id TEXT, added_at TEXT, listened_at TEXT, rating REAL, updated_at TEXT, deleted INTEGER") =>
        $"CREATE TABLE items ({columns})";

    // ── the desktop database ──

    [Fact]
    public void The_desktop_queue_comes_in_with_its_uids_dates_and_ratings()
    {
        var listened = Album("Spinetta Jade", "Bajo Belgrano", "uid-2");
        listened.ListenedAt = new DateTime(2026, 2, 3, 0, 0, 0, DateTimeKind.Utc);
        listened.Rating = 4.5;
        var bytes = DesktopDb(Album("Charly García", "Clics modernos", "uid-1"), listened);

        var report = _transfer.Import("alice", bytes);

        Assert.Equal(new ImportReport(2, 0, 0), report);
        var items = _stores.QueueOf("alice").LoadAll().OrderBy(i => i.Uid).ToList();
        Assert.Equal(["uid-1", "uid-2"], items.Select(i => i.Uid));
        Assert.Equal(("Charly García", "Clics modernos", 1983, "Rolling Stone 214"), (items[0].Artist, items[0].Album, items[0].Year, items[0].Source));
        Assert.Equal((4.5, true), (items[1].Rating, items[1].IsListened));
        Assert.Equal(("sp1", "td1"), (items[0].SpotifyId, items[0].TidalId));
    }

    [Fact]
    public void Importing_the_same_file_twice_adds_nothing_the_second_time()
    {
        var bytes = DesktopDb(Album("A", "One", "u1"), Album("B", "Two", "u2"));
        _transfer.Import("alice", bytes);

        var again = _transfer.Import("alice", bytes);

        Assert.Equal(new ImportReport(0, 2, 0), again);
        Assert.Equal(2, _stores.QueueOf("alice").LoadAll().Count);
    }

    [Fact]
    public void A_newer_row_in_the_file_updates_the_one_here_and_an_older_one_does_not()
    {
        var store = _stores.QueueOf("alice");
        var here = Album("A", "Here", "u1");
        store.Insert(here);

        var stamp = (DateTime d) => d.ToString("O");
        var newer = RawDb(ItemsTable(), $"INSERT INTO items (uid, artist, album, added_at, rating, updated_at, deleted) VALUES ('u1','A','Newer title','{stamp(DateTime.UtcNow.AddDays(1))}',-1,'{stamp(DateTime.UtcNow.AddDays(1))}',0)");
        var stale = RawDb(ItemsTable(), $"INSERT INTO items (uid, artist, album, added_at, rating, updated_at, deleted) VALUES ('u1','A','Stale title','{stamp(DateTime.UtcNow.AddDays(-5))}',-1,'{stamp(DateTime.UtcNow.AddDays(-5))}',0)");

        Assert.Equal(new ImportReport(0, 1, 0), _transfer.Import("alice", stale));
        Assert.Equal("Here", store.FindByUid("u1")!.Album);

        Assert.Equal(new ImportReport(0, 1, 0), _transfer.Import("alice", newer));
        Assert.Equal("Newer title", store.FindByUid("u1")!.Album);
    }

    [Fact]
    public void The_files_own_last_edit_time_is_kept_so_a_newer_backup_imported_later_still_wins()
    {
        string Backup(string album, double rating, string updated) => JsonSerializer.Serialize(new
        {
            format = "queueify-export", version = 1,
            albums = new[] { new { uid = "u1", artist = "A", album, rating, addedAt = "2026-01-01T00:00:00Z", updatedAt = updated } },
        });

        _transfer.Import("alice", Encoding.UTF8.GetBytes(Backup("Old title", -1, "2026-01-02T00:00:00Z")));
        var stored = _stores.QueueOf("alice").FindByUid("u1")!;
        Assert.Equal(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), stored.UpdatedAt);

        _transfer.Import("alice", Encoding.UTF8.GetBytes(Backup("New title", 5, "2026-06-01T00:00:00Z")));

        var after = _stores.QueueOf("alice").FindByUid("u1")!;
        Assert.Equal(("New title", 5.0), (after.Album, after.Rating));
    }

    [Fact]
    public void An_updated_time_in_the_future_or_without_a_zone_cannot_beat_real_edits()
    {
        var future = JsonSerializer.Serialize(new { format = "queueify-export", version = 1, albums = new[] { new { uid = "u1", artist = "A", album = "One", addedAt = "2026-01-01T00:00:00Z", updatedAt = "2999-01-01T00:00:00" } } });
        _transfer.Import("alice", Encoding.UTF8.GetBytes(future));
        Assert.True(_stores.QueueOf("alice").FindByUid("u1")!.UpdatedAt <= DateTime.UtcNow);
    }

    [Fact]
    public void JSON_saved_with_a_byte_order_mark_still_imports()
    {
        _stores.QueueOf("alice").Insert(Album("A", "One", "u1"));
        var json = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(_transfer.ExportJson("alice"))).ToArray();

        Assert.Equal(1, _transfer.Import("bob", json).Added);
    }

    [Fact]
    public void A_table_with_a_generated_column_is_refused_because_reading_it_could_cost_gigabytes()
    {
        var bytes = RawDb("CREATE TABLE items (id INTEGER PRIMARY KEY, artist TEXT, album TEXT, added_at TEXT, cover_url TEXT AS (hex(zeroblob(400000000))))",
            $"INSERT INTO items (artist, album, added_at) VALUES ('A','B','{DateTime.UtcNow:O}')");

        Assert.Equal(ImportProblem.Invalid, ProblemOf(bytes));
    }

    [Fact]
    public void A_virtual_table_named_items_is_refused()
    {
        var bytes = RawDb("CREATE VIRTUAL TABLE items USING fts5(artist, album, added_at)");
        Assert.Equal(ImportProblem.Invalid, ProblemOf(bytes));
    }

    [Fact]
    public void A_large_import_is_one_quick_transaction()
    {
        var albums = Enumerable.Range(0, 3000).Select(i => new { uid = $"u{i}", artist = "A", album = $"Album {i}", addedAt = "2026-01-01T00:00:00Z" });
        var json = JsonSerializer.Serialize(new { format = "queueify-export", version = 1, albums });

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var report = _transfer.Import("alice", Encoding.UTF8.GetBytes(json));

        Assert.Equal(3000, report.Added);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task Two_imports_at_once_for_one_user_are_not_allowed_to_collide()
    {
        var albums = Enumerable.Range(0, 3000).Select(i => new { uid = $"u{i}", artist = "A", album = $"Album {i}", addedAt = "2026-01-01T00:00:00Z" });
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { format = "queueify-export", version = 1, albums }));

        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            try { _transfer.Import("alice", json); return (ImportProblem?)null; }
            catch (ImportException ex) { return ex.Problem; }
        }, TestContext.Current.CancellationToken)));

        Assert.All(results, r => Assert.True(r is null or ImportProblem.Busy));
        Assert.Equal(3000, _stores.QueueOf("alice").LoadAll().Count);
    }

    [Fact]
    public void An_album_the_user_deleted_here_stays_deleted()
    {
        var store = _stores.QueueOf("alice");
        var item = Album("A", "One", "u1");
        store.Insert(item);
        store.Delete(item);

        var report = _transfer.Import("alice", DesktopDb(Album("A", "One", "u1")));

        Assert.Equal(0, report.Added);
        Assert.Empty(store.LoadAll());
    }

    [Fact]
    public void Albums_deleted_in_the_file_are_not_brought_in()
    {
        var bytes = RawDb(ItemsTable(), $"INSERT INTO items (uid, artist, album, added_at, rating, deleted) VALUES ('u1','A','One','{DateTime.UtcNow:O}',-1,1)");
        Assert.Equal(new ImportReport(0, 0, 1), _transfer.Import("alice", bytes));
    }

    [Fact]
    public void A_database_from_before_sync_without_uids_still_imports_and_does_not_duplicate()
    {
        var bytes = RawDb("CREATE TABLE items (id INTEGER PRIMARY KEY AUTOINCREMENT, artist TEXT NOT NULL, album TEXT NOT NULL, year INTEGER NULL, source TEXT NULL, cover_url TEXT NULL, spotify_id TEXT NULL, tidal_id TEXT NULL, added_at TEXT NOT NULL, listened_at TEXT NULL, rating REAL NOT NULL DEFAULT -1)",
            $"INSERT INTO items (artist, album, added_at) VALUES ('Old','Disc','{DateTime.UtcNow:O}')");

        Assert.Equal(1, _transfer.Import("alice", bytes).Added);
        Assert.Equal(new ImportReport(0, 1, 0), _transfer.Import("alice", bytes));
        Assert.Single(_stores.QueueOf("alice").LoadAll());
    }

    [Fact]
    public void A_wal_mode_database_copied_without_its_side_files_can_be_read()
    {
        var path = Path.Combine(_root, "wal.db");
        Directory.CreateDirectory(_root);
        using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            db.Open();
            foreach (var sql in new[] { "PRAGMA journal_mode=WAL", ItemsTable(), $"INSERT INTO items (uid, artist, album, added_at, rating, deleted) VALUES ('w1','A','One','{DateTime.UtcNow:O}',-1,0)" })
            { using var c = db.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
            using var cp = db.CreateCommand(); cp.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)"; cp.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        Assert.Equal(1, _transfer.Import("alice", File.ReadAllBytes(path)).Added);
    }

    // ── a hostile database ──

    [Fact]
    public void Garbage_is_not_a_queue() => Assert.Equal(ImportProblem.Invalid, ProblemOf("MZ\u0090\0 not a database at all"u8.ToArray()));

    [Fact]
    public void An_empty_upload_is_not_a_queue() => Assert.Equal(ImportProblem.Invalid, ProblemOf([]));

    [Fact]
    public void A_database_cut_short_is_refused_without_a_crash()
    {
        var bytes = DesktopDb(Album("A", "One", "u1"));
        Assert.Equal(ImportProblem.Invalid, ProblemOf(bytes[..20]));
        Assert.Equal(ImportProblem.Invalid, ProblemOf(bytes[..(bytes.Length / 2)]));
    }

    [Fact]
    public void A_database_with_some_other_table_is_not_a_queue() =>
        Assert.Equal(ImportProblem.Invalid, ProblemOf(RawDb("CREATE TABLE secrets (a TEXT)", "INSERT INTO secrets VALUES ('x')")));

    [Fact]
    public void A_view_named_items_is_refused_because_it_could_run_anything_when_read() =>
        Assert.Equal(ImportProblem.Invalid, ProblemOf(RawDb("CREATE TABLE other (artist, album, added_at)", "CREATE VIEW items AS SELECT artist, album, added_at FROM other")));

    [Fact]
    public void A_table_without_the_needed_columns_is_refused() =>
        Assert.Equal(ImportProblem.Invalid, ProblemOf(RawDb("CREATE TABLE items (artist TEXT)")));

    [Fact]
    public void An_enormous_text_is_cut_not_loaded()
    {
        var bytes = RawDb(ItemsTable(), $"INSERT INTO items (uid, artist, album, added_at, rating, deleted) VALUES ('u1', printf('%.*c', 3000000, 'x'), 'Album','{DateTime.UtcNow:O}',-1,0)");

        _transfer.Import("alice", bytes);

        Assert.Equal(500, _stores.QueueOf("alice").FindByUid("u1")!.Artist.Length);
    }

    [Fact]
    public void Odd_types_and_bad_values_skip_that_row_and_keep_the_rest()
    {
        var now = DateTime.UtcNow.ToString("O");
        var bytes = RawDb(ItemsTable("uid, artist, album, year, added_at, rating, deleted"),
            $"INSERT INTO items VALUES ('ok','A','One',1983,'{now}',3,0)",
            $"INSERT INTO items VALUES ('blob', x'0102', x'0304', 'abc', '{now}', 'high', 0)",
            "INSERT INTO items VALUES ('nodate','A','Two',1990,'not a date',-1,0)",
            "INSERT INTO items VALUES (NULL, NULL, NULL, NULL, NULL, NULL, NULL)",
            $"INSERT INTO items VALUES ('empty','','',1990,'{now}',-1,0)",
            $"INSERT INTO items VALUES ('year','A','Three',99999,'{now}',99,0)");

        var report = _transfer.Import("alice", bytes);

        var kept = _stores.QueueOf("alice").LoadAll().ToDictionary(i => i.Uid);
        Assert.Contains("ok", kept.Keys);
        Assert.DoesNotContain("nodate", kept.Keys);
        Assert.DoesNotContain("empty", kept.Keys);
        Assert.Null(kept["year"].Year);
        Assert.Equal(-1, kept["year"].Rating);   // out of range: unrated
        Assert.Equal(6, report.Added + report.AlreadyHere + report.Skipped);
    }

    [Fact]
    public void A_trigger_or_a_second_table_in_the_file_does_nothing_because_it_is_only_read()
    {
        var bytes = RawDb(ItemsTable(), "CREATE TABLE audit (n)",
            "CREATE TRIGGER t AFTER INSERT ON items BEGIN INSERT INTO audit VALUES (1); END",
            $"INSERT INTO items (uid, artist, album, added_at, rating, deleted) VALUES ('u1','A','One','{DateTime.UtcNow:O}',-1,0)");

        Assert.Equal(1, _transfer.Import("alice", bytes).Added);
    }

    [Fact]
    public void More_rows_than_any_queue_is_refused()
    {
        var bytes = RawDb(ItemsTable("uid, artist, album, added_at, rating, deleted"),
            $"WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i < {DataTransfer.MaxRows + 5}) INSERT INTO items SELECT i, 'A', 'B', '{DateTime.UtcNow:O}', -1, 0 FROM n");
        Assert.Equal(ImportProblem.TooLarge, ProblemOf(bytes));
    }

    [Fact]
    public void An_upload_over_the_size_limit_is_refused_before_it_is_opened() =>
        Assert.Equal(ImportProblem.TooLarge, ProblemOf(new byte[DataTransfer.MaxBytes + 1]));

    [Fact]
    public void No_temporary_copy_of_the_upload_is_left_behind()
    {
        var before = Directory.GetFiles(Path.GetTempPath(), "queueify-import-*");
        _transfer.Import("alice", DesktopDb(Album("A", "One", "u1")));
        Assert.Empty(Directory.GetFiles(Path.GetTempPath(), "queueify-import-*").Except(before));

        Assert.Throws<ImportException>(() => _transfer.Import("alice", "SQLite format 3\0 and then nonsense"u8.ToArray()));
        Assert.Empty(Directory.GetFiles(Path.GetTempPath(), "queueify-import-*").Except(before));
    }

    // ── JSON ──

    [Fact]
    public void An_exported_queue_imports_into_another_account_with_the_same_uids()
    {
        var store = _stores.QueueOf("alice");
        var a = Album("Charly García", "Clics modernos", "u1");
        a.ListenedAt = new DateTime(2026, 3, 3, 0, 0, 0, DateTimeKind.Utc);
        a.Rating = 5;
        store.Insert(a);
        store.Insert(Album("Spinetta Jade", "Bajo Belgrano", "u2"));
        var json = Encoding.UTF8.GetBytes(_transfer.ExportJson("alice"));

        var report = _transfer.Import("bob", json);

        Assert.Equal(new ImportReport(2, 0, 0), report);
        var copy = _stores.QueueOf("bob").FindByUid("u1")!;
        Assert.Equal(("Charly García", 5.0), (copy.Artist, copy.Rating));
        Assert.True(copy.IsListened);
        Assert.Equal(new ImportReport(0, 2, 0), _transfer.Import("bob", json));
    }

    [Fact]
    public void The_export_leaves_out_deleted_albums_and_keeps_no_secrets()
    {
        var store = _stores.QueueOf("alice");
        var gone = Album("Gone", "Away", "u9");
        store.Insert(gone);
        store.Delete(gone);
        store.Insert(Album("Here", "Now", "u1"));
        _stores.SaveSettings("alice", new AppSettings { SpotifyClientId = "SPOTID", SpotifyClientSecret = "SPOTSECRET" });

        var json = _transfer.ExportJson("alice");

        Assert.DoesNotContain("Gone", json);
        Assert.DoesNotContain("SPOT", json);
        Assert.Contains("\"Here\"", json);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("""{"format":"something-else","albums":[]}""")]
    [InlineData("""{"format":"queueify-export","version":99,"albums":[]}""")]
    [InlineData("""{"format":"queueify-export","version":1,"albums":"x"}""")]
    public void JSON_that_is_not_an_export_is_refused(string json) =>
        Assert.Equal(ImportProblem.Invalid, ProblemOf(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void JSON_rows_are_cut_and_clamped_like_database_rows()
    {
        var json = JsonSerializer.Serialize(new
        {
            format = "queueify-export",
            version = 1,
            albums = new object[]
            {
                new { uid = "j1", artist = new string('x', 9000), album = "A", year = 1990, rating = 99, addedAt = "2026-01-01T00:00:00Z", deleted = false },
                new { uid = "j2", artist = "", album = "", addedAt = "2026-01-01T00:00:00Z" },
                new { uid = "j3", artist = "Z", album = "A", addedAt = "garbage" },
            },
        });

        var report = _transfer.Import("alice", Encoding.UTF8.GetBytes(json));

        Assert.Equal(new ImportReport(1, 0, 2), report);
        var item = _stores.QueueOf("alice").FindByUid("j1")!;
        Assert.Equal((500, -1.0), (item.Artist.Length, item.Rating));
    }

    // ── CSV ──

    [Fact]
    public void The_csv_quotes_what_needs_it_and_defuses_spreadsheet_formulas()
    {
        var store = _stores.QueueOf("alice");
        store.Insert(Album("Comma, Inc", "He said \"hi\"\nnewline", "u1"));
        store.Insert(Album("=HYPERLINK(\"http://evil\")", "+cmd|' /C calc'!A0", "u2"));

        var csv = _transfer.ExportCsv("alice");

        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("artist,album,year,source,added,listened,rating,spotify_id,tidal_id,uid", lines[0]);
        Assert.Contains("\"Comma, Inc\"", csv);
        Assert.Contains("\"He said \"\"hi\"\"\nnewline\"", csv);
        Assert.DoesNotContain(",=HYPERLINK", csv);
        Assert.DoesNotContain("\n=HYPERLINK", csv);
        Assert.Contains("'=HYPERLINK", csv);
        Assert.Contains("'+cmd", csv);
    }

    [Fact]
    public void An_empty_queue_exports_just_the_header()
    {
        Assert.Single(_transfer.ExportCsv("nobody").Split("\r\n", StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("\"albums\":[]", _transfer.ExportJson("nobody"));
    }
}
