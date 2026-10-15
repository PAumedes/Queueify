using Queueify.Models;
using Queueify.Services;
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
    public async Task AStaleTab_CannotBringADeletedAlbumBack()
    {
        var a = QueueOf("alice");
        await a.LoadAsync();
        var item = a.Add(new BacklogItem { Album = "x" }, "");
        var staleCopy = a.All.Single();

        var b = QueueOf("alice");
        await b.LoadAsync();
        b.Delete(b.All.Single());

        a.Rate(staleCopy, 4);
        a.SetListened(staleCopy, true);

        var check = QueueOf("alice");
        await check.LoadAsync();
        Assert.Empty(check.All);
        Assert.Equal(item.Uid, staleCopy.Uid);
    }

    [Fact]
    public async Task Undo_DoesNotUndoAnotherTabsListened()
    {
        var a = QueueOf("alice");
        await a.LoadAsync();
        a.Add(new BacklogItem { Album = "x" }, "");
        var staleCopy = a.All.Single();

        var b = QueueOf("alice");
        await b.LoadAsync();
        b.SetListened(b.All.Single(), true);          // already listened in the other tab

        var undo = a.SetListened(staleCopy, true);    // this tab still believed it was pending
        undo();

        var check = QueueOf("alice");
        await check.LoadAsync();
        Assert.True(check.All.Single().IsListened);
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

    // ── a change updates the one album in memory; it does not read the whole queue again ──

    [Fact]
    public async Task AChange_UpdatesTheShownAlbumWithoutReadingEveryAlbumAgain()
    {
        var q = QueueOf("alice");
        await q.LoadAsync();
        var item = q.Add(new BacklogItem { Album = "mine" }, "");
        // Written behind this queue's back: a full reload would show it, a one-album update does not.
        new UserStores(_root).QueueOf("alice").Insert(new BacklogItem { Album = "elsewhere", AddedAt = DateTime.Now });

        q.Rate(item, 4);
        Assert.Equal(["mine"], q.All.Select(i => i.Album));
        Assert.Equal(4, q.All.Single().Rating);

        var undo = q.SetListened(item, true);
        Assert.True(q.All.Single().IsListened);
        undo();
        Assert.True(q.All.Single().IsPending);

        var undoDelete = q.Delete(item);
        Assert.Empty(q.All);
        undoDelete();
        Assert.Equal(["mine"], q.All.Select(i => i.Album));

        q.Remove(item);
        Assert.Empty(q.All);
    }

    [Fact]
    public async Task LoadAsync_IsTheExplicitRefresh_AndSeesWhatWasWrittenElsewhere()
    {
        var q = QueueOf("alice");
        await q.LoadAsync();
        new UserStores(_root).QueueOf("alice").Insert(new BacklogItem { Album = "elsewhere", AddedAt = DateTime.Now });
        await q.LoadAsync();
        Assert.Equal(["elsewhere"], q.All.Select(i => i.Album));
    }

    [Fact]
    public async Task Counts_AreKeptInStepWithTheChanges()
    {
        var q = QueueOf("alice");
        await q.LoadAsync();
        var a = q.Add(new BacklogItem { Album = "a" }, "");
        q.Add(new BacklogItem { Album = "b" }, "");
        q.SetListened(a, true);
        q.Delete(a);
        Assert.Equal((1, 0), (q.Pending, q.Heard));
        Assert.Equal(["b"], q.PendingItems.Select(i => i.Album));
        Assert.Empty(q.HeardItems);
    }

    // ── searching with a key worked out once per album ──

    [Theory]
    [InlineData("garcia")]
    [InlineData("GARCÍA")]
    [InlineData("clics 1983")]
    [InlineData("pelo")]
    [InlineData("  ")]
    [InlineData("zzz")]
    [InlineData("charly clics")]
    public async Task Search_FindsWhatTheSharedQueueViewFinds(string typed)
    {
        var q = QueueOf("alice");
        await q.LoadAsync();
        q.Add(new BacklogItem { Artist = "Charly García", Album = "Clics Modernos", Year = 1983 }, "Pelo");
        q.Add(new BacklogItem { Artist = "Soda Stereo", Album = "Signos", Year = 1986 }, "Rolling Stone");
        var heard = q.Add(new BacklogItem { Artist = "Spinetta", Album = "Artaud", Year = 1973 }, "");
        q.SetListened(heard, true);

        var expected = QueueView.Apply(q.All, false, typed, "oldest").Select(i => i.Uid);
        var actual = QueueView.Apply(q.Search(typed), false, "", "oldest").Select(i => i.Uid);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Search_SeesAnAlbumAddedOrChangedLater()
    {
        var q = QueueOf("alice");
        await q.LoadAsync();
        Assert.Empty(q.Search("signos"));
        var item = q.Add(new BacklogItem { Artist = "Soda Stereo", Album = "Signos" }, "");
        Assert.Single(q.Search("signos"));
        q.Delete(item);
        Assert.Empty(q.Search("signos"));
    }

    private sealed class FixedUser(string? id) : IUserContext
    {
        public Task<string?> UserIdAsync() => Task.FromResult(id);
    }
}
