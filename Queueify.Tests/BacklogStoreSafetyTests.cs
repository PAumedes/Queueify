using Microsoft.Data.Sqlite;
using Queueify.Models;
using Queueify.Services;

namespace Queueify.Tests;

/// <summary>How <see cref="BacklogStore"/> behaves with several writers (sync, imports, web pages) and with times that are not UTC.</summary>
public sealed class BacklogStoreSafetyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queueify-tests", Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_dir, "queueify.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static readonly DateTime Old = new(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime New = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static BacklogItem Item(string uid, string album, DateTime updated, bool deleted = false) => new()
    {
        Uid = uid, Artist = "A", Album = album, AddedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        UpdatedAt = updated, Deleted = deleted,
    };

    private SqliteConnection Raw()
    {
        var db = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        db.Open();
        return db;
    }

    private static void Run(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ───────────── one-time schema work, WAL ─────────────

    [Fact]
    public void The_database_uses_a_write_ahead_log_so_readers_and_a_writer_do_not_block_each_other()
    {
        _ = new BacklogStore(DbPath);

        using var db = Raw();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", cmd.ExecuteScalar());
    }

    [Fact]
    public void Opening_a_known_database_again_does_no_schema_work_so_it_never_waits_for_a_writer()
    {
        _ = new BacklogStore(DbPath);
        using var writer = Raw();
        Run(writer, "BEGIN IMMEDIATE");   // another sync or import holds the write lock

        var started = DateTime.UtcNow;
        var again = new BacklogStore(DbPath);
        var shown = again.LoadAll();      // reads are not blocked in WAL mode

        Assert.Empty(shown);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void A_database_deleted_and_created_again_in_the_same_process_gets_its_schema_again()
    {
        var first = new BacklogStore(DbPath);
        first.Insert(Item("u1", "One", Old));
        SqliteConnection.ClearAllPools();
        File.Delete(DbPath);
        foreach (var extra in Directory.GetFiles(_dir, "queueify.db-*")) File.Delete(extra);

        var second = new BacklogStore(DbPath);

        Assert.Empty(second.LoadAll());
        second.Insert(Item("u2", "Two", Old));
        Assert.Single(second.LoadAll());
    }

    // ───────────── times are stored as UTC ─────────────

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void A_backup_album_with_a_non_UTC_edit_time_is_not_dirty_for_ever_once_uploaded(DateTimeKind kind)
    {
        var store = new BacklogStore(DbPath);
        var edited = kind == DateTimeKind.Local ? Old.ToLocalTime() : DateTime.SpecifyKind(Old, DateTimeKind.Unspecified);

        store.MergeBackup([Item("u1", "One", edited)]);
        var stored = store.FindByUid("u1")!;
        store.MarkUploaded(stored);

        Assert.Equal(Old, stored.UpdatedAt);
        Assert.False(store.FindByUid("u1")!.Dirty);
    }


    // ───────────── MergeBackup ─────────────

    [Fact]
    public void MergeBackup_gives_an_album_without_an_id_a_new_one()
    {
        var store = new BacklogStore(DbPath);
        var noId = Item("", "No id", Old);

        store.MergeBackup([noId]);

        Assert.Equal(32, noId.Uid.Length);
        Assert.Equal("No id", Assert.Single(store.LoadAll()).Album);
    }

    [Fact]
    public void MergeBackup_overwrites_every_field_of_an_older_album_and_queues_it_for_upload()
    {
        var store = new BacklogStore(DbPath);
        store.MergeBackup([Item("u1", "Old", Old)]);
        store.MarkUploaded(store.FindByUid("u1")!);
        var newer = new BacklogItem
        {
            Uid = "u1", Artist = "Other artist", Album = "New", Year = 1999, Source = "Mag", CoverUrl = "https://c/x.jpg",
            SpotifyId = "sp1", TidalId = "td1", AddedAt = new DateTime(2025, 5, 5, 0, 0, 0, DateTimeKind.Utc),
            ListenedAt = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc), Rating = 4, UpdatedAt = New, Deleted = true,
        };

        store.MergeBackup([newer]);

        var s = store.FindByUid("u1")!;
        Assert.Equal(("Other artist", "New", 1999, "Mag"), (s.Artist, s.Album, s.Year, s.Source));
        Assert.Equal(("https://c/x.jpg", "sp1", "td1"), (s.CoverUrl, s.SpotifyId, s.TidalId));
        Assert.Equal(new DateTime(2025, 5, 5, 0, 0, 0, DateTimeKind.Utc), s.AddedAt.ToUniversalTime());
        Assert.Equal(new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc), s.ListenedAt!.Value.ToUniversalTime());
        Assert.Equal(4, s.Rating);
        Assert.Equal(New, s.UpdatedAt);
        Assert.True(s.Dirty);
        Assert.False(s.Deleted);   // a backup never brings a deletion with it
    }

    [Fact]
    public void MergeBackup_counts_an_id_that_appears_twice_once_as_added_and_once_as_already_here()
    {
        var store = new BacklogStore(DbPath);

        var result = store.MergeBackup([Item("u1", "First", Old), Item("u1", "Second", New)]);

        Assert.Equal((1, 1), result);
        Assert.Equal("Second", store.FindByUid("u1")!.Album);
    }

    [Fact]
    public void MergeBackup_that_fails_halfway_changes_nothing()
    {
        var store = new BacklogStore(DbPath);
        IEnumerable<BacklogItem> Broken()
        {
            yield return Item("u1", "One", Old);
            throw new InvalidOperationException("boom");
        }

        Assert.Throws<InvalidOperationException>(() => store.MergeBackup(Broken()));

        Assert.Empty(store.LoadAll());
    }

    [Fact]
    public async Task MergeBackup_takes_the_write_lock_first_so_a_sync_writing_meanwhile_cannot_make_it_fail()
    {
        var store = new BacklogStore(DbPath);
        store.MergeBackup([Item("known", "Known", New)]);

        Task? sync = null;
        IEnumerable<BacklogItem> Slow()
        {
            yield return Item("known", "Older copy", Old);   // looked up, then skipped: a read before any write
            // A sync commits a write now. Had MergeBackup not locked the file yet, its read is out of date and its next
            // write is refused at once (SQLITE_BUSY_SNAPSHOT: waiting cannot help). With the lock taken first, the
            // sync waits for MergeBackup instead.
            sync = Task.Run(() => new BacklogStore(DbPath).Insert(Item("other", "Other", New)));
            sync.Wait(TimeSpan.FromMilliseconds(600));
            yield return Item("fresh", "Fresh", New);
        }

        var result = await Task.Run(() => store.MergeBackup(Slow()));

        Assert.Equal((1, 1), result);
        await sync!;
        Assert.Equal(["Fresh", "Known", "Other"], store.LoadAll().Select(i => i.Album).Order());
    }

    // ───────────── ApplyRemoteBatch ─────────────

    [Fact]
    public void ApplyRemoteBatch_adds_updates_and_removes_in_one_go_and_counts_what_changed()
    {
        var store = new BacklogStore(DbPath);
        store.MergeBackup([Item("update", "Old", Old), Item("gone", "Gone", Old), Item("newer here", "Mine", New)]);

        var applied = store.ApplyRemoteBatch(
        [
            Item("update", "Updated", New),
            Item("gone", "Gone", New, deleted: true),
            Item("newer here", "Theirs", Old),          // older than ours: kept out
            Item("added", "Added", New),
            Item("never seen", "Never seen", New, deleted: true),
        ]);

        Assert.Equal(3, applied);
        Assert.Equal(["Added", "Mine", "Updated"], store.LoadAll().Select(i => i.Album).Order());
        Assert.False(store.FindByUid("added")!.Dirty);
    }

    [Fact]
    public void ApplyRemoteBatch_that_fails_halfway_applies_none_of_it()
    {
        var store = new BacklogStore(DbPath);
        IEnumerable<BacklogItem> Broken()
        {
            yield return Item("u1", "One", New);
            yield return Item("u2", "Two", New);
            throw new InvalidOperationException("boom");
        }

        Assert.Throws<InvalidOperationException>(() => store.ApplyRemoteBatch(Broken()));

        Assert.Empty(store.LoadAll());
    }

    [Fact]
    public void ApplyRemoteBatch_of_nothing_is_fine()
    {
        Assert.Equal(0, new BacklogStore(DbPath).ApplyRemoteBatch([]));
    }
}
