using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Queueify.Models;
using Queueify.Services;
using Queueify.Web.Sync;

namespace Queueify.Web.Tests.Sync;

/// <summary>
/// The desktop client (<see cref="ServerSync"/>, from Core) against the real server (<see cref="SyncEngine"/>), in
/// process. The fake server in Queueify.Tests re-implements the server's rules and can drift from it; these pin the two
/// together. Each PC is a scratch <see cref="BacklogStore"/>; users are unique per test, so the host is shared.
/// </summary>
public sealed class ServerSyncEndToEndTests(SyncFactory app) : IClassFixture<SyncFactory>, IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qw-e2e-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>One desktop PC: its own database and settings, and the real client with an in-process connection.</summary>
    private sealed class Pc(string path, string token, HttpClient http)
    {
        public readonly string Path = path;
        public readonly BacklogStore Store = new(path);
        public readonly AppSettings Settings = new() { WebServerUrl = "http://localhost", WebToken = token };
        private readonly ServerSync _client = new(http);

        public Task<SyncResult> SyncAsync() => _client.SyncAsync(Store, Settings, CancellationToken.None);

        public BacklogItem Add(string artist, string album)
        {
            var item = new BacklogItem { Artist = artist, Album = album, Year = 1973, AddedAt = DateTime.Now };
            Store.Insert(item);
            return item;
        }

        public void Edit(string uid, Action<BacklogItem> change)
        {
            var item = Store.FindByUid(uid)!;
            change(item);
            Store.Update(item);
        }

        public IEnumerable<(string, string, string, int?, double, DateTime, DateTime?)> Snapshot() =>
            Store.LoadAll().OrderBy(i => i.Uid).Select(i => (i.Uid, i.Artist, i.Album, i.Year, i.Rating, i.UpdatedAt, i.ListenedAt?.ToUniversalTime()));
    }

    private Pc NewPc(string token) => new(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db"), token, app.CreateClient());

    private static void Execute(string path, string sql)
    {
        using var db = new SqliteConnection($"Data Source={path};Pooling=False");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ───────────── two PCs and the web ─────────────

    [Fact]
    public async Task Two_PCs_and_the_web_pages_end_up_with_the_same_queue()
    {
        var (userId, token) = await app.NewUserAsync("e2e");
        var pc1 = NewPc(token);
        var pc2 = NewPc(token);
        pc1.Add("Sonic Youth", "Daydream Nation");
        pc1.Add("Spinetta", "Artaud");
        await pc1.SyncAsync();

        var first = await pc2.SyncAsync();
        Assert.Equal(2, first.Downloaded);
        Assert.Equal(pc1.Snapshot(), pc2.Snapshot());

        // the web page adds an album through the same store the pages use
        app.Stores.QueueOf(userId).Insert(new BacklogItem { Artist = "Charly García", Album = "Clics Modernos", Year = 1983, AddedAt = DateTime.UtcNow });
        var uid = pc2.Store.LoadAll().First(i => i.Album == "Artaud").Uid;
        pc2.Edit(uid, i => { i.Rating = 5; i.ListenedAt = DateTime.Now; });
        await pc2.SyncAsync();
        await pc1.SyncAsync();
        await pc2.SyncAsync();

        Assert.Equal(3, pc1.Store.LoadAll().Count);
        Assert.Equal(pc1.Snapshot(), pc2.Snapshot());
        Assert.Equal(5, pc1.Store.FindByUid(uid)!.Rating);
        Assert.Empty(pc1.Store.LoadDirty());
        Assert.Empty(pc2.Store.LoadDirty());
        Assert.Equal(0, (await pc1.SyncAsync()).Downloaded);   // nothing left to say
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task When_two_PCs_edit_the_same_album_the_later_edit_wins_whichever_syncs_first(bool theNewerEditSyncsFirst)
    {
        var (_, token) = await app.NewUserAsync("conflict");
        var pc1 = NewPc(token);
        var pc2 = NewPc(token);
        var uid = pc1.Add("Virus", "Locura").Uid;
        await pc1.SyncAsync();
        await pc2.SyncAsync();

        pc1.Edit(uid, i => i.Album = "Older edit");
        pc2.Edit(uid, i => i.Album = "Newer edit");
        if (theNewerEditSyncsFirst) { await pc2.SyncAsync(); await pc1.SyncAsync(); }
        else { await pc1.SyncAsync(); await pc2.SyncAsync(); }
        await pc1.SyncAsync();
        await pc2.SyncAsync();

        Assert.Equal("Newer edit", pc1.Store.FindByUid(uid)!.Album);
        Assert.Equal(pc1.Snapshot(), pc2.Snapshot());
        Assert.Empty(pc1.Store.LoadDirty());
        Assert.Empty(pc2.Store.LoadDirty());
    }

    // ───────────── deletions ─────────────

    [Fact]
    public async Task A_deletion_reaches_the_other_PC_and_is_forgotten_by_the_one_that_made_it()
    {
        var (_, token) = await app.NewUserAsync("delete");
        var pc1 = NewPc(token);
        var pc2 = NewPc(token);
        var gone = pc1.Add("Gone", "Soon");
        pc1.Add("Stays", "Here");
        await pc1.SyncAsync();
        await pc2.SyncAsync();

        pc1.Store.Delete(gone);
        await pc1.SyncAsync();
        await pc2.SyncAsync();

        Assert.Equal(["Here"], pc2.Store.LoadAll().Select(i => i.Album));
        Assert.Null(pc2.Store.FindByUid(gone.Uid));
        Assert.Null(pc1.Store.FindByUid(gone.Uid));   // the uploaded deletion is removed for good on the PC
        Assert.Equal(pc1.Snapshot(), pc2.Snapshot());
    }

    [Fact]
    public async Task A_PC_that_joins_later_never_sees_an_album_that_was_already_deleted()
    {
        var (_, token) = await app.NewUserAsync("late");
        var pc1 = NewPc(token);
        var gone = pc1.Add("Gone", "Soon");
        pc1.Add("Stays", "Here");
        await pc1.SyncAsync();
        pc1.Store.Delete(gone);
        await pc1.SyncAsync();

        var late = NewPc(token);
        var result = await late.SyncAsync();

        Assert.Equal(["Here"], late.Store.LoadAll().Select(i => i.Album));
        Assert.Equal(1, result.Downloaded);
        Assert.Null(late.Store.FindByUid(gone.Uid));
    }

    [Fact]
    public async Task An_edit_made_after_a_deletion_on_another_PC_brings_the_album_back()
    {
        var (_, token) = await app.NewUserAsync("undelete");
        var pc1 = NewPc(token);
        var pc2 = NewPc(token);
        var album = pc1.Add("Virus", "Locura");
        await pc1.SyncAsync();
        await pc2.SyncAsync();

        pc1.Store.Delete(album);
        pc2.Edit(album.Uid, i => i.Rating = 4);   // later than the deletion
        await pc1.SyncAsync();
        await pc2.SyncAsync();
        await pc1.SyncAsync();

        Assert.Equal(4, pc1.Store.FindByUid(album.Uid)!.Rating);
        Assert.Equal(pc1.Snapshot(), pc2.Snapshot());
    }

    // ───────────── paging ─────────────

    private static BacklogItem Backup(int i) => new()
    {
        Uid = "u" + i.ToString("D5"), Artist = "Artist " + i, Album = "Album " + i, AddedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(i),
    };

    [Fact]
    public async Task A_queue_of_more_than_1000_albums_goes_up_and_comes_down_in_pages()
    {
        var (_, token) = await app.NewUserAsync("big");
        var pc1 = NewPc(token);
        pc1.Store.MergeBackup(Enumerable.Range(0, 2500).Select(Backup));

        var up = await pc1.SyncAsync();
        var pc2 = NewPc(token);
        var down = await pc2.SyncAsync();

        Assert.Equal(2500, up.Uploaded);
        Assert.Equal(2500, down.Downloaded);
        Assert.Equal(2500, pc2.Store.LoadAll().Count);
        Assert.Equal(pc1.Snapshot(), pc2.Snapshot());
        Assert.Equal(0, (await pc2.SyncAsync()).Downloaded);
    }

    [Fact]
    public async Task A_new_PC_gets_the_albums_even_when_the_first_page_holds_only_old_deletions()
    {
        var (_, token) = await app.NewUserAsync("tombstones");
        var pc1 = NewPc(token);
        pc1.Store.MergeBackup(Enumerable.Range(0, 1200).Select(Backup));
        // The first 1100 are deleted here, so the server's oldest 1000 rows are all deletions.
        Execute(pc1.Path, $"UPDATE items SET deleted = 1, dirty = 1, updated_at = '{BacklogStore.Now():O}' WHERE rowid <= 1100");
        await pc1.SyncAsync();

        var pc2 = NewPc(token);
        await pc2.SyncAsync();

        Assert.Equal(100, pc2.Store.LoadAll().Count);
        Assert.Equal(pc1.Store.LoadAll().Count, pc2.Store.LoadAll().Count);
    }

    // ───────────── settings and tokens ─────────────

    [Fact]
    public async Task The_first_PC_with_keys_makes_them_the_shared_keys_and_the_next_PC_receives_them()
    {
        var (_, token) = await app.NewUserAsync("settings");
        var pc1 = NewPc(token);
        pc1.Settings.SpotifyClientId = "spotify-id";
        pc1.Settings.SpotifyClientSecret = "spotify-secret";
        pc1.Settings.PreferredService = "Spotify";
        pc1.Settings.CountryCode = "AR";
        pc1.Settings.SharedSettingsUpdatedAt = null;
        await pc1.SyncAsync();

        var pc2 = NewPc(token);
        var result = await pc2.SyncAsync();

        Assert.True(result.SettingsChanged);
        Assert.Equal(("spotify-id", "spotify-secret", "Spotify", "AR"),
            (pc2.Settings.SpotifyClientId, pc2.Settings.SpotifyClientSecret, pc2.Settings.PreferredService, pc2.Settings.CountryCode));
    }

    [Fact]
    public async Task A_revoked_token_stops_the_sync_with_a_plain_message()
    {
        var (userId, _) = await app.NewUserAsync("revoked");
        NewToken created;
        using (var scope = app.Services.CreateScope())
            created = await scope.ServiceProvider.GetRequiredService<PersonalAccessTokens>().CreateAsync(userId, "second PC");
        var pc = NewPc(created.Token);
        await pc.SyncAsync();

        using (var scope = app.Services.CreateScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<PersonalAccessTokens>().RevokeAsync(userId, created.Info.Id));
        pc.Add("After", "Revocation");

        var ex = await Assert.ThrowsAsync<SyncException>(() => pc.SyncAsync());

        Assert.Equal(Loc.T("webSync.err.unauthorized"), ex.Message);
        Assert.DoesNotContain(created.Token, ex.Message);
        Assert.Single(pc.Store.LoadDirty());   // nothing was lost: it is sent when a valid token is entered
    }

    [Fact]
    public async Task Text_cut_on_upload_never_ends_in_half_an_emoji()
    {
        var (_, token) = await app.NewUserAsync("emoji");
        var pc1 = NewPc(token);
        pc1.Add("Artist", new string('a', ServerSync.MaxTextLength - 1) + "\U0001F600" + "tail");

        await pc1.SyncAsync();
        var pc2 = NewPc(token);
        await pc2.SyncAsync();

        var album = Assert.Single(pc2.Store.LoadAll()).Album;
        Assert.DoesNotContain(album, c => char.IsSurrogate(c));
        Assert.Equal(new string('a', ServerSync.MaxTextLength - 1), album);
    }
}

/// <summary>Edge cases of the server's merge that the contract tests did not pin, found by removing one condition at a time.</summary>
public sealed class SyncEngineEdgeTests(SyncFactory app) : IClassFixture<SyncFactory>
{
    private static readonly DateTime T = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private static SyncAlbum Wire(string uid, string album, DateTime updated, string artist = "Artist", bool deleted = false) =>
        new(uid, artist, album, 1999, null, null, null, null, T, null, -1, updated, deleted);

    private async Task<SyncResponse> Pull(string token, DateTime? since = null) =>
        (await app.SyncAsync(token, new SyncRequest(since, [], null))).Body!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_same_album_twice_in_one_request_keeps_the_newer_whichever_comes_first(bool newerFirst)
    {
        var (_, token) = await app.NewUserAsync("dup");
        var older = Wire("dup1", "Older", T);
        var newer = Wire("dup1", "Newer", T.AddMinutes(1));

        var (status, _) = await app.SyncAsync(token, new SyncRequest(null, newerFirst ? [newer, older] : [older, newer], null));

        Assert.Equal(System.Net.HttpStatusCode.OK, status);
        Assert.Equal("Newer", Assert.Single((await Pull(token)).Albums).Album);
    }

    [Fact]
    public async Task Text_is_cut_at_500_characters_without_leaving_half_a_surrogate_pair()
    {
        var (_, token) = await app.NewUserAsync("cut");
        var text = new string('a', SyncEngine.MaxText - 1) + "\U0001F600" + "tail";

        await app.SyncAsync(token, new SyncRequest(null, [Wire("cut1", text, T, artist: text)], null));

        var stored = Assert.Single((await Pull(token)).Albums);
        Assert.Equal(new string('a', SyncEngine.MaxText - 1), stored.Album);
        Assert.Equal(new string('a', SyncEngine.MaxText - 1), stored.Artist);
    }

    [Fact]
    public async Task A_deletion_made_on_the_web_and_not_yet_sent_to_any_PC_is_not_purged_however_old()
    {
        var (userId, token) = await app.NewUserAsync("purge");
        var first = await Pull(token);   // creates the server's columns in the user's file
        var item = new BacklogItem { Artist = "Gone", Album = "Soon", AddedAt = DateTime.UtcNow };
        var queue = app.Stores.QueueOf(userId);
        queue.Insert(item);
        queue.Delete(item);
        // Older than the 90 days tombstones are kept, but never stamped: no PC has been told yet.
        using (var db = new SqliteConnection($"Data Source={app.Stores.QueuePathOf(userId)};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE items SET updated_at = $old WHERE uid = $uid";
            cmd.Parameters.AddWithValue("$old", DateTime.UtcNow.AddDays(-120).ToString("O"));
            cmd.Parameters.AddWithValue("$uid", item.Uid);
            cmd.ExecuteNonQuery();
        }

        var answer = await Pull(token, first.ServerTime);

        var tombstone = Assert.Single(answer.Albums);
        Assert.Equal(item.Uid, tombstone.Uid);
        Assert.True(tombstone.Deleted);
    }

    [Fact]
    public async Task Settings_saved_on_the_web_are_sent_once_not_with_every_sync()
    {
        var (userId, token) = await app.NewUserAsync("seen");
        var settings = app.Stores.LoadSettings(userId);
        settings.SpotifyClientId = "web-id";
        settings.SpotifyClientSecret = "web-secret";
        settings.SharedSettingsUpdatedAt = DateTime.UtcNow;
        app.Stores.SaveSettings(userId, settings);

        var first = await Pull(token);
        var second = await Pull(token, first.ServerTime);

        Assert.Equal("web-id", first.Settings!.SpotifyClientId);
        Assert.Null(second.Settings);
    }
}
