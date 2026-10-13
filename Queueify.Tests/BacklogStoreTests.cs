using Microsoft.Data.Sqlite;
using Queueify.Models;
using Queueify.Services;

namespace Queueify.Tests;

public sealed class BacklogStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queueify-tests", Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_dir, "queueify.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static BacklogItem Album(string album) =>
        new() { Artist = "Charly García", Album = album, Year = 1983, AddedAt = DateTime.Now };

    [Fact]
    public void Insert_AssignsIdsAndMarksForUpload()
    {
        var store = new BacklogStore(DbPath);
        var item = Album("Clics Modernos");
        store.Insert(item);

        var loaded = Assert.Single(store.LoadAll());
        Assert.Equal(item.Uid, loaded.Uid);
        Assert.Equal(32, loaded.Uid.Length);
        Assert.Equal("Charly García", loaded.Artist);
        Assert.True(loaded.Dirty);
        Assert.Single(store.LoadDirty());
    }

    [Fact]
    public void Delete_HidesTheAlbumButKeepsItUntilUploaded()
    {
        var store = new BacklogStore(DbPath);
        var item = Album("Clics Modernos");
        store.Insert(item);
        store.MarkUploaded(store.FindByUid(item.Uid)!);

        store.Delete(item);

        Assert.Empty(store.LoadAll());
        var pending = Assert.Single(store.LoadDirty());
        Assert.True(pending.Deleted);

        store.MarkUploaded(pending);
        Assert.Null(store.FindByUid(item.Uid));
    }

    [Fact]
    public void Restore_BringsBackAnAlbumEvenAfterTheDeletionWasUploaded()
    {
        var store = new BacklogStore(DbPath);
        var item = Album("Clics Modernos");
        store.Insert(item);
        store.Delete(item);
        store.MarkUploaded(store.LoadDirty().Single());

        store.Restore(item);

        var back = Assert.Single(store.LoadAll());
        Assert.Equal(item.Uid, back.Uid);
        Assert.True(back.Dirty);
    }

    [Fact]
    public void MarkUploaded_KeepsChangesMadeWhileUploading()
    {
        var store = new BacklogStore(DbPath);
        var item = Album("Clics Modernos");
        store.Insert(item);
        var uploaded = store.FindByUid(item.Uid)!;

        Thread.Sleep(2);
        item.Rating = 5;
        store.Update(item);
        store.MarkUploaded(uploaded);

        Assert.True(store.FindByUid(item.Uid)!.Dirty);
    }

    [Fact]
    public void ApplyRemote_AddsUpdatesAndDeletesByUid()
    {
        var store = new BacklogStore(DbPath);
        var remote = Album("Piano Bar");
        remote.Uid = Guid.NewGuid().ToString("N");
        remote.UpdatedAt = BacklogStore.Now();

        store.ApplyRemote(remote);
        Assert.False(store.FindByUid(remote.Uid)!.Dirty);

        // Each later change from the other PC carries a newer timestamp.
        remote.ListenedAt = DateTime.Now;
        remote.UpdatedAt = remote.UpdatedAt.AddSeconds(1);
        store.ApplyRemote(remote);
        Assert.NotNull(store.FindByUid(remote.Uid)!.ListenedAt);

        remote.Deleted = true;
        remote.UpdatedAt = remote.UpdatedAt.AddSeconds(1);
        store.ApplyRemote(remote);
        Assert.Null(store.FindByUid(remote.Uid));
    }

    [Fact]
    public void MarkAllDirty_QueuesEveryAlbumForUpload()
    {
        // Reconnecting sync (or joining another queue) must upload albums that were already synced before.
        var store = new BacklogStore(DbPath);
        foreach (var name in new[] { "Clics Modernos", "Piano Bar" }) store.Insert(Album(name));
        foreach (var item in store.LoadDirty()) store.MarkUploaded(item);
        Assert.Empty(store.LoadDirty());

        store.MarkAllDirty();

        Assert.Equal(2, store.LoadDirty().Count);
    }

    [Fact]
    public void ApplyRemote_NeverOverwritesANewerLocalEdit()
    {
        // A sync runs in the background: the user may edit an album between the sync's check and its write.
        var store = new BacklogStore(DbPath);
        var local = Album("Clics Modernos");
        store.Insert(local);
        var olderRemote = store.FindByUid(local.Uid)!;
        olderRemote.Source = "older copy from another PC";
        olderRemote.UpdatedAt = local.UpdatedAt.AddSeconds(-10);

        store.ApplyRemote(olderRemote);

        Assert.Null(store.FindByUid(local.Uid)!.Source);
        Assert.True(store.FindByUid(local.Uid)!.Dirty);
    }

    [Fact]
    public void ApplyRemote_AnOlderRemoteDeletionDoesNotDeleteANewerLocalEdit()
    {
        var store = new BacklogStore(DbPath);
        var local = Album("Clics Modernos");
        store.Insert(local);
        var deletedLongAgo = store.FindByUid(local.Uid)!;
        deletedLongAgo.Deleted = true;
        deletedLongAgo.UpdatedAt = local.UpdatedAt.AddSeconds(-10);

        store.ApplyRemote(deletedLongAgo);

        Assert.NotNull(store.FindByUid(local.Uid));
    }

    [Fact]
    public void OpeningADatabaseFromVersion1_AddsSyncColumnsAndKeepsAlbums()
    {
        Directory.CreateDirectory(_dir);
        using (var db = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE items (id INTEGER PRIMARY KEY AUTOINCREMENT, artist TEXT NOT NULL, album TEXT NOT NULL,
                    year INTEGER NULL, source TEXT NULL, cover_url TEXT NULL, spotify_id TEXT NULL, tidal_id TEXT NULL,
                    added_at TEXT NOT NULL, listened_at TEXT NULL, rating REAL NOT NULL DEFAULT -1);
                INSERT INTO items (artist, album, added_at) VALUES ('Virus', 'Locura', '2026-10-11T03:00:00.0000000-03:00');
                """;
            cmd.ExecuteNonQuery();
        }

        var store = new BacklogStore(DbPath);

        var item = Assert.Single(store.LoadAll());
        Assert.Equal("Locura", item.Album);
        Assert.Equal(32, item.Uid.Length);
        Assert.True(item.Dirty);
    }
}
