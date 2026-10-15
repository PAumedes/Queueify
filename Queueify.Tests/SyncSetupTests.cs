using Microsoft.Data.Sqlite;
using Queueify.Models;
using Queueify.Services;

namespace Queueify.Tests;

/// <summary>Connecting to an online queue is tried on a copy of the settings (nothing half-connected on failure).</summary>
public sealed class SyncSetupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queueify-tests", Guid.NewGuid().ToString("N"));
    private BacklogStore NewStore() => new(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private sealed class Recorder(Exception? failWith = null) : ISyncProvider
    {
        public readonly List<(AppSettings Settings, int DirtyAtStart)> Calls = [];

        public Task<SyncResult> SyncAsync(BacklogStore store, AppSettings settings, CancellationToken ct)
        {
            Calls.Add((settings, store.LoadDirty().Count));
            return failWith == null ? Task.FromResult(new SyncResult(0, 0, false)) : Task.FromException<SyncResult>(failWith);
        }
    }

    private static BacklogItem Item(string album) => new() { Artist = "A", Album = album, AddedAt = DateTime.Now };

    [Fact]
    public async Task A_cloud_connection_syncs_everything_with_the_new_queue_and_leaves_the_live_settings_alone()
    {
        var store = NewStore();
        store.Insert(Item("One"));
        store.MarkUploaded(store.LoadDirty().Single());   // synced to an earlier queue: clean
        var live = new AppSettings();
        Recorder cloud = new(), web = new();

        var trial = await SyncSetup.TryConnectAsync(store, live, SyncTarget.JoinCloud("proj-1a2b3", "abc"), cloud, web, null, default);

        Assert.Equal(SyncKind.Cloud, trial.ActiveSync);
        Assert.Equal(SyncKind.None, live.ActiveSync);
        Assert.Same(trial, cloud.Calls.Single().Settings);
        Assert.Empty(web.Calls);
        Assert.Equal(1, cloud.Calls[0].DirtyAtStart);
    }

    [Fact]
    public async Task A_web_connection_uses_the_web_provider()
    {
        Recorder cloud = new(), web = new();

        var trial = await SyncSetup.TryConnectAsync(NewStore(), new AppSettings(), SyncTarget.Web("https://q.example.com", "qw_token"), cloud, web, null, default);

        Assert.Equal(SyncKind.Web, trial.ActiveSync);
        Assert.Equal(("https://q.example.com", "qw_token"), (trial.WebServerUrl, trial.WebToken));
        Assert.Empty(cloud.Calls);
        Assert.Single(web.Calls);
    }

    [Fact]
    public async Task A_failed_first_sync_throws_and_nothing_is_connected()
    {
        var live = new AppSettings();
        Recorder cloud = new(new SyncException("no")), web = new();

        await Assert.ThrowsAsync<SyncException>(() =>
            SyncSetup.TryConnectAsync(NewStore(), live, SyncTarget.JoinCloud("proj-1a2b3", "abc"), cloud, web, null, default));

        Assert.False(live.SyncConfigured);
    }

    [Theory]
    [InlineData(false, true)]    // creating a queue with keys on this PC: the keys go up
    [InlineData(true, false)]    // joining: the queue's keys win
    public async Task Keys_go_up_only_when_creating_a_queue(bool joining, bool keysStamped)
    {
        var live = new AppSettings { SpotifyClientId = "id", SpotifyClientSecret = "secret" };
        Recorder cloud = new(), web = new();
        var target = joining ? SyncTarget.JoinCloud("proj-1a2b3", "abc") : SyncTarget.CreateCloud("proj-1a2b3", "abc");

        var trial = await SyncSetup.TryConnectAsync(NewStore(), live, target, cloud, web, null, default);

        Assert.Equal(keysStamped, trial.SharedSettingsUpdatedAt != null);
    }

    [Fact]
    public async Task Creating_a_queue_without_keys_stamps_nothing()
    {
        Recorder cloud = new(), web = new();

        var trial = await SyncSetup.TryConnectAsync(NewStore(), new AppSettings(), SyncTarget.CreateCloud("proj-1a2b3", "abc"), cloud, web, null, default);

        Assert.Null(trial.SharedSettingsUpdatedAt);
    }

    [Fact]
    public async Task A_sync_still_finishing_ends_before_everything_is_marked_for_upload()
    {
        var store = NewStore();
        store.Insert(Item("One"));
        store.MarkUploaded(store.LoadDirty().Single());
        var previous = new TaskCompletionSource();
        Recorder cloud = new(), web = new();

        var connecting = SyncSetup.TryConnectAsync(store, new AppSettings(), SyncTarget.JoinCloud("proj-1a2b3", "abc"), cloud, web, previous.Task, default);
        await Task.Delay(100, TestContext.Current.CancellationToken);   // nothing may happen while the earlier sync is running
        Assert.False(connecting.IsCompleted);
        Assert.Empty(cloud.Calls);
        Assert.Empty(store.LoadDirty());

        previous.SetResult();
        await connecting;

        Assert.Equal(1, cloud.Calls.Single().DirtyAtStart);
    }

    [Fact]
    public async Task An_earlier_sync_that_failed_does_not_stop_the_connection()
    {
        var failed = Task.FromException(new SyncException("old failure"));
        Recorder cloud = new(), web = new();

        var trial = await SyncSetup.TryConnectAsync(NewStore(), new AppSettings(), SyncTarget.JoinCloud("proj-1a2b3", "abc"), cloud, web, failed, default);

        Assert.True(trial.SyncConfigured);
    }
}
