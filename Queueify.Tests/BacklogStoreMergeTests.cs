using Queueify.Models;
using Queueify.Services;

namespace Queueify.Tests;

public sealed class BacklogStoreMergeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qm-" + Guid.NewGuid().ToString("N"));
    private BacklogStore Store() => new(Path.Combine(_dir, "queueify.db"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static BacklogItem Item(string uid, string album, DateTime updated) =>
        new() { Uid = uid, Artist = "A", Album = album, AddedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), UpdatedAt = updated };

    private static readonly DateTime Old = new(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime New = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void MergeBackup_AddsNewAlbumsKeepingTheirUidAndEditTime()
    {
        var store = Store();

        Assert.Equal((1, 0), store.MergeBackup([Item("u1", "One", Old)]));

        var stored = store.FindByUid("u1")!;
        Assert.Equal(Old, stored.UpdatedAt);
        Assert.False(stored.Dirty);
    }

    [Fact]
    public void MergeBackup_NewerCopyWinsAndOlderOrEqualDoesNot()
    {
        var store = Store();
        store.MergeBackup([Item("u1", "Old title", Old)]);

        store.MergeBackup([Item("u1", "Same time", Old)]);
        Assert.Equal("Old title", store.FindByUid("u1")!.Album);

        Assert.Equal((0, 1), store.MergeBackup([Item("u1", "New title", New)]));
        Assert.Equal("New title", store.FindByUid("u1")!.Album);
    }

    [Fact]
    public void MergeBackup_DoesNotBringBackAnAlbumDeletedHere()
    {
        var store = Store();
        var item = Item("u1", "One", Old);
        store.Insert(item);
        store.Delete(item);

        store.MergeBackup([Item("u1", "One", DateTime.UtcNow.AddDays(1))]);

        Assert.Empty(store.LoadAll());
    }
}
