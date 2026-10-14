using Queueify.Models;
using Queueify.Web.Services;
using Queueify.Web.Storage;

namespace Queueify.Web.Tests;

public sealed class UserQueueTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qw-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private UserQueue QueueOf(string user) => new(new UserStores(_root), new FixedUser(user));

    [Fact]
    public async Task Add_StoresTheAlbumWithItsMagazineAndRemembersTheMagazine()
    {
        var q = QueueOf("alice");
        await q.LoadAsync();

        var item = q.Add(new BacklogItem { Artist = "Soda Stereo", Album = "Signos" }, "  Pelo ");

        Assert.Equal("Pelo", item.Source);
        Assert.Equal("Pelo", new UserStores(_root).LoadSettings("alice").LastSource);
        var again = QueueOf("alice");
        await again.LoadAsync();
        Assert.Equal("Signos", Assert.Single(again.All).Album);
    }

    [Fact]
    public async Task Add_WithNoMagazine_LeavesItBlank()
    {
        var q = QueueOf("alice");
        await q.LoadAsync();
        Assert.Null(q.Add(new BacklogItem { Album = "x" }, "  ").Source);
    }

    [Fact]
    public async Task Users_NeverSeeEachOthersAlbums()
    {
        var alice = QueueOf("alice");
        await alice.LoadAsync();
        alice.Add(new BacklogItem { Album = "Private" }, "");

        var bob = QueueOf("bob");
        await bob.LoadAsync();
        Assert.Empty(bob.All);
    }

    [Fact]
    public async Task NobodySignedIn_HasNoQueue_AndCannotAdd()
    {
        var q = new UserQueue(new UserStores(_root), new FixedUser(null));
        await q.LoadAsync();
        Assert.Empty(q.All);
        Assert.Throws<InvalidOperationException>(() => q.Add(new BacklogItem { Album = "x" }, ""));
    }

    [Fact]
    public async Task SetListened_MovesTheAlbum_AndUndoPutsItBack()
    {
        var q = QueueOf("alice");
        await q.LoadAsync();
        var item = q.Add(new BacklogItem { Album = "x" }, "");

        var undo = q.SetListened(item, true);
        Assert.True(q.All.Single().IsListened);

        undo();
        Assert.True(q.All.Single().IsPending);
    }

    [Fact]
    public async Task Undo_UsesTheStoredVersion_SoANewerChangeSurvives()
    {
        var q = QueueOf("alice");
        await q.LoadAsync();
        var item = q.Add(new BacklogItem { Album = "x" }, "");
        var undo = q.SetListened(item, true);

        // Another tab rated it in the meantime.
        var other = QueueOf("alice");
        await other.LoadAsync();
        other.Rate(other.All.Single(), 4);

        undo();
        var final = QueueOf("alice");
        await final.LoadAsync();
        Assert.True(final.All.Single().IsPending);
        Assert.Equal(4, final.All.Single().Rating);
    }

    [Fact]
    public async Task Delete_RemovesTheAlbum_AndUndoRestoresIt()
    {
        var q = QueueOf("alice");
        await q.LoadAsync();
        var item = q.Add(new BacklogItem { Album = "x" }, "");

        var undo = q.Delete(item);
        Assert.Empty(q.All);

        undo();
        Assert.Single(q.All);
    }

    [Fact]
    public async Task Rate_IsSaved()
    {
        var q = QueueOf("alice");
        await q.LoadAsync();
        var item = q.Add(new BacklogItem { Album = "x" }, "");
        q.SetListened(item, true);
        q.Rate(item, 5);

        var again = QueueOf("alice");
        await again.LoadAsync();
        Assert.Equal(5, again.All.Single().Rating);
    }

    [Fact]
    public async Task EveryChange_RaisesChanged()
    {
        var q = QueueOf("alice");
        await q.LoadAsync();
        int raised = 0;
        q.Changed += () => raised++;
        var item = q.Add(new BacklogItem { Album = "x" }, "");
        q.SetListened(item, true);
        q.Rate(item, 3);
        q.Delete(item);
        Assert.Equal(4, raised);
    }

    [Fact]
    public async Task Counts_SplitPendingAndListened()
    {
        var q = QueueOf("alice");
        await q.LoadAsync();
        q.Add(new BacklogItem { Album = "a" }, "");
        var b = q.Add(new BacklogItem { Album = "b" }, "");
        q.SetListened(b, true);
        Assert.Equal((1, 1), (q.Pending, q.Heard));
    }

    private sealed class FixedUser(string? id) : IUserContext
    {
        public Task<string?> UserIdAsync() => Task.FromResult(id);
    }
}
