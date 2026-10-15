using Microsoft.Data.Sqlite;
using Queueify.Models;
using Queueify.Services;

namespace Queueify.Tests;

/// <summary>Sync codes and project ids: no network needed.</summary>
public class CloudSyncCodeTests
{
    [Fact]
    public void SyncCode_RoundTrips()
    {
        var s = new AppSettings { CloudProjectId = "queueify-1a2b3", CloudQueueKey = CloudSync.NewQueueKey() };
        Assert.True(CloudSync.TryParseSyncCode(CloudSync.SyncCode(s), out var project, out var key));
        Assert.Equal(s.CloudProjectId, project);
        Assert.Equal(s.CloudQueueKey, key);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("queueify:Bad Project:0123456789abcdef0123456789abcdef")]
    [InlineData("queueify:queueify-1a2b3:tooshort")]
    public void TryParseSyncCode_RejectsGarbage(string code)
    {
        Assert.False(CloudSync.TryParseSyncCode(code, out _, out _));
    }

    [Fact]
    public void SecurityRules_AreTheRulesFileTestedAgainstTheEmulator()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "tools", "firestore.rules");
        Assert.Equal(File.ReadAllText(file), CloudSync.SecurityRules);
        Assert.Contains("match /queues/{queueKey}/albums/{albumId}", CloudSync.SecurityRules);
    }

    [Fact]
    public void CopySharedFrom_TakesKeysAndPreferencesButNotThisPcsOwnValues()
    {
        var mine = new AppSettings { LastSource = "Rolling Stone #3", CloudQueueKey = "mine", SpotifyClientId = "old" };
        var synced = new AppSettings
        {
            SpotifyClientId = "s1", SpotifyClientSecret = "s2", TidalClientId = "t1", TidalClientSecret = "t2",
            PreferredService = "Spotify", CountryCode = "ES", SharedSettingsUpdatedAt = DateTime.UtcNow,
            LastSource = "other", CloudQueueKey = "other",
        };

        mine.CopySharedFrom(synced);

        Assert.Equal(("s1", "s2", "t1", "t2"), (mine.SpotifyClientId, mine.SpotifyClientSecret, mine.TidalClientId, mine.TidalClientSecret));
        Assert.Equal(("Spotify", "ES", synced.SharedSettingsUpdatedAt), (mine.PreferredService, mine.CountryCode, mine.SharedSettingsUpdatedAt));
        Assert.Equal(("Rolling Stone #3", "mine"), (mine.LastSource, mine.CloudQueueKey));
    }

    [Fact]
    public void ApplyEdits_KeepsNewerSyncedValuesTheUserDidNotTouch()
    {
        // Settings dialog opened with old keys; a sync then brought new keys; the user only changed Country.
        var opened = new AppSettings { SpotifyClientId = "old", CountryCode = "AR" };
        var live = new AppSettings { SpotifyClientId = "new-from-other-pc", CountryCode = "AR" };
        var edited = opened.Clone();
        edited.CountryCode = "ES";

        bool changed = live.ApplyEdits(opened, edited);

        Assert.True(changed);
        Assert.Equal(("new-from-other-pc", "ES"), (live.SpotifyClientId, live.CountryCode));
    }

    [Theory]
    [InlineData(" ar ", "spotify", "AR", "Spotify")]
    [InlineData("", "", "AR", "Tidal")]
    [InlineData("ARG", "Deezer", "AR", "Tidal")]
    [InlineData("es", "TIDAL", "ES", "Tidal")]
    public void Normalize_GivesCountryAndPreferredAppTheirStandardForm(string country, string preferred, string expectedCountry, string expectedPreferred)
    {
        var s = new AppSettings { CountryCode = country, PreferredService = preferred };

        s.Normalize();

        Assert.Equal((expectedCountry, expectedPreferred), (s.CountryCode, s.PreferredService));
    }

    [Fact]
    public void ForSettingsPage_PickingTheShownCountryAgain_IsNoChange()
    {
        // Saved "ar" shows as Argentina; the dropdown hands back "AR". That isn't an edit to upload.
        var live = new AppSettings { CountryCode = "ar", PreferredService = "spotify" };
        var shown = live.ForSettingsPage();
        var edited = shown.Clone();
        edited.CountryCode = "AR";
        edited.PreferredService = "Spotify";

        Assert.False(live.ApplyEdits(shown, edited));
    }

    [Fact]
    public void ApplyEdits_ReportsNoChangeWhenNothingWasEdited()
    {
        var opened = new AppSettings { SpotifyClientId = "old" };
        var live = new AppSettings { SpotifyClientId = "new-from-other-pc" };

        Assert.False(live.ApplyEdits(opened, opened.Clone()));
        Assert.Equal("new-from-other-pc", live.SpotifyClientId);
    }

    [Fact]
    public void AppSettingsClone_IsIndependent()
    {
        var original = new AppSettings { SpotifyClientId = "a", CloudQueueKey = "k", CloudLastServerTime = DateTime.UtcNow };
        var copy = original.Clone();
        copy.SpotifyClientId = "b";

        Assert.Equal("a", original.SpotifyClientId);
        Assert.Equal(("k", original.CloudLastServerTime), (copy.CloudQueueKey, copy.CloudLastServerTime));
    }

    [Theory]
    [InlineData("queueify-1a2b3", "queueify-1a2b3")]
    [InlineData("  Queueify-1A2B3 ", "queueify-1a2b3")]
    [InlineData("https://console.firebase.google.com/u/0/project/queueify-1a2b3/firestore", "queueify-1a2b3")]
    [InlineData("not a project", null)]
    public void NormalizeProjectId_AcceptsIdsAndConsoleUrls(string input, string? expected)
    {
        Assert.Equal(expected, CloudSync.NormalizeProjectId(input));
    }
}

/// <summary>
/// Two simulated PCs syncing through the local Firestore emulator with Queueify's security rules.
/// Skipped unless QUEUEIFY_FIRESTORE_EMULATOR is set (see README, "Running the tests").
/// </summary>
public sealed class CloudSyncEmulatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queueify-tests", Guid.NewGuid().ToString("N"));
    private readonly CloudSync _cloud = new(new HttpClient());
    private readonly string _key = CloudSync.NewQueueKey();

    public CloudSyncEmulatorTests()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("QUEUEIFY_FIRESTORE_EMULATOR") is { Length: > 0 },
            "Set QUEUEIFY_FIRESTORE_EMULATOR to run Firebase sync tests against the local emulator.");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private BacklogStore Store(string name) => new(Path.Combine(_dir, name + ".db"));
    private AppSettings Pc() => new() { CloudProjectId = "demo-queueify", CloudQueueKey = _key };

    private static BacklogItem Album(string artist, string album, int year) =>
        new() { Artist = artist, Album = album, Year = year, AddedAt = DateTime.Now, SpotifyId = "sp-" + album };

    [Fact]
    public async Task AlbumsKeysAndAccents_ReachTheSecondPc()
    {
        var (a, b) = (Store("a"), Store("b"));
        var setA = Pc();
        setA.SpotifyClientId = "sp-id";
        setA.SpotifyClientSecret = "sp-secret";
        setA.SharedSettingsUpdatedAt = BacklogStore.Now();
        var setB = Pc();
        var clics = Album("Charly García", "Clics Modernos", 1983);
        a.Insert(clics);
        a.Insert(Album("Spinetta Jade", "Bajo Belgrano", 1983));

        var up = await _cloud.SyncAsync(a, setA, default);
        var down = await _cloud.SyncAsync(b, setB, default);

        Assert.Equal(2, up.Uploaded);
        Assert.Empty(a.LoadDirty());
        Assert.Equal(2, down.Downloaded);
        Assert.True(down.SettingsChanged);
        Assert.Equal(("sp-id", "sp-secret"), (setB.SpotifyClientId, setB.SpotifyClientSecret));
        var copy = b.FindByUid(clics.Uid)!;
        Assert.Equal(("Charly García", "Clics Modernos", (int?)1983), (copy.Artist, copy.Album, copy.Year));
    }

    [Fact]
    public async Task IdleSync_DownloadsAndUploadsNothing()
    {
        var a = Store("a");
        var set = Pc();
        a.Insert(Album("Virus", "Locura", 1985));
        await _cloud.SyncAsync(a, set, default);

        var again = await _cloud.SyncAsync(a, set, default);

        Assert.Equal((0, 0), (again.Downloaded, again.Uploaded));
    }

    [Fact]
    public async Task ListenedRatingsAndDeletions_SyncBothWays()
    {
        var (a, b) = (Store("a"), Store("b"));
        var (setA, setB) = (Pc(), Pc());
        var clics = Album("Charly García", "Clics Modernos", 1983);
        var bajo = Album("Spinetta Jade", "Bajo Belgrano", 1983);
        a.Insert(clics);
        a.Insert(bajo);
        await _cloud.SyncAsync(a, setA, default);
        await _cloud.SyncAsync(b, setB, default);

        var onB = b.FindByUid(clics.Uid)!;
        onB.ListenedAt = DateTime.Now;
        onB.Rating = 5;
        b.Update(onB);
        b.Delete(b.FindByUid(bajo.Uid)!);
        await _cloud.SyncAsync(b, setB, default);
        await _cloud.SyncAsync(a, setA, default);

        var onA = a.FindByUid(clics.Uid)!;
        Assert.NotNull(onA.ListenedAt);
        Assert.Equal(5, onA.Rating);
        Assert.Null(a.FindByUid(bajo.Uid));
        Assert.Null(b.FindByUid(bajo.Uid));
    }

    [Fact]
    public async Task Conflict_TheNewerEditWinsOnBothPcs()
    {
        var (a, b) = (Store("a"), Store("b"));
        var (setA, setB) = (Pc(), Pc());
        var vasos = Album("Los Fabulosos Cadillacs", "Vasos Vacíos", 1993);
        a.Insert(vasos);
        await _cloud.SyncAsync(a, setA, default);
        await _cloud.SyncAsync(b, setB, default);

        var onA = a.FindByUid(vasos.Uid)!;
        onA.Source = "Rolling Stone #12";
        a.Update(onA);
        SpinWait.SpinUntil(() => BacklogStore.Now() > onA.UpdatedAt);   // the second edit must carry a later stamp
        var onB = b.FindByUid(vasos.Uid)!;
        onB.Source = "Inrockuptibles #3";
        b.Update(onB);
        await _cloud.SyncAsync(a, setA, default);
        await _cloud.SyncAsync(b, setB, default);
        await _cloud.SyncAsync(a, setA, default);

        Assert.Equal("Inrockuptibles #3", a.FindByUid(vasos.Uid)!.Source);
        Assert.Equal("Inrockuptibles #3", b.FindByUid(vasos.Uid)!.Source);
    }

    [Fact]
    public async Task UndoDelete_AfterUpload_BringsTheAlbumBackEverywhere()
    {
        var (a, b) = (Store("a"), Store("b"));
        var (setA, setB) = (Pc(), Pc());
        var item = Album("Virus", "Locura", 1985);
        a.Insert(item);
        await _cloud.SyncAsync(a, setA, default);
        await _cloud.SyncAsync(b, setB, default);

        a.Delete(item);
        await _cloud.SyncAsync(a, setA, default);
        await _cloud.SyncAsync(b, setB, default);
        Assert.Null(b.FindByUid(item.Uid));

        a.Restore(item);
        await _cloud.SyncAsync(a, setA, default);
        await _cloud.SyncAsync(b, setB, default);
        Assert.NotNull(b.FindByUid(item.Uid));
    }

    [Fact]
    public async Task JoiningAQueueThatHasNoSettings_UploadsThisPcsKeys()
    {
        // PC A created the queue before entering any keys; PC B joins with keys; PC C should get them.
        await _cloud.SyncAsync(Store("a"), Pc(), default);
        var setB = Pc();
        setB.TidalClientId = "td-id";
        setB.TidalClientSecret = "td-secret";
        await _cloud.SyncAsync(Store("b"), setB, default);

        var setC = Pc();
        await _cloud.SyncAsync(Store("c"), setC, default);

        Assert.Equal(("td-id", "td-secret"), (setC.TidalClientId, setC.TidalClientSecret));
    }

    [Fact]
    public async Task AVeryLongText_IsShortenedInsteadOfBlockingEveryUpload()
    {
        // The rules refuse text over 500 characters; one pasted paragraph must not block the whole batch forever.
        var (a, b) = (Store("a"), Store("b"));
        var item = Album("Virus", "Locura", 1985);
        item.Source = new string('x', 2000);
        a.Insert(item);
        a.Insert(Album("Soda Stereo", "Signos", 1986));

        var up = await _cloud.SyncAsync(a, Pc(), default);
        await _cloud.SyncAsync(b, Pc(), default);

        Assert.Equal(2, up.Uploaded);
        Assert.Empty(a.LoadDirty());
        Assert.Equal(CloudSync.MaxTextLength, b.FindByUid(item.Uid)!.Source!.Length);
    }

    // ── Security rules, checked with raw REST calls like an outsider would make ──

    private static string Host => $"http://{Environment.GetEnvironmentVariable("QUEUEIFY_FIRESTORE_EMULATOR")}/v1/projects/demo-queueify/databases/(default)/documents";

    private static async Task<HttpResponseMessage> Write(string path, string fieldsJson)
    {
        using var http = new HttpClient();
        var body = new StringContent($$"""{"fields":{{fieldsJson}}}""", System.Text.Encoding.UTF8, "application/json");
        return await http.PatchAsync($"{Host}/{path}", body);
    }

    [Fact]
    public async Task SecurityRules_OnlyAllowAlbumsAndSettingsUnderAQueue()
    {
        var album = """{"uid":{"stringValue":"x"},"artist":{"stringValue":"Virus"},"album":{"stringValue":"Locura"}}""";

        Assert.True((await Write($"queues/{_key}/albums/x", album)).IsSuccessStatusCode, "a normal album is accepted");
        Assert.False((await Write($"queues/{_key}", album)).IsSuccessStatusCode, "the queue document itself is off limits");
        Assert.False((await Write($"queues/{_key}/junk/x", album)).IsSuccessStatusCode, "other collections are refused");
        Assert.False((await Write($"queues/{_key}/albums/x", """{"payload":{"stringValue":"anything"}}""")).IsSuccessStatusCode,
            "unknown fields are refused");
        var huge = "{\"uid\":{\"stringValue\":\"x\"},\"artist\":{\"stringValue\":\"" + new string('a', 5000)
                   + "\"},\"album\":{\"stringValue\":\"y\"}}";
        Assert.False((await Write($"queues/{_key}/albums/x", huge)).IsSuccessStatusCode, "oversized text is refused");
    }

    [Fact]
    public async Task SecurityRules_DoNotRevealOtherQueues()
    {
        await _cloud.SyncAsync(Store("a"), Pc(), default);
        using var http = new HttpClient();

        var list = await http.GetAsync($"{Host}/queues?showMissing=true");

        Assert.False(list.IsSuccessStatusCode, $"listing queues returned {(int)list.StatusCode}: {await list.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task SecurityRules_RefuseShortKeys_AndOtherKeysSeeNothing()
    {
        var a = Store("a");
        a.Insert(Album("Virus", "Locura", 1985));
        await _cloud.SyncAsync(a, Pc(), default);

        var refused = await Assert.ThrowsAsync<CatalogException>(() =>
            _cloud.SyncAsync(Store("c"), new AppSettings { CloudProjectId = "demo-queueify", CloudQueueKey = "short" }, default));
        Assert.Contains("security rules", refused.Message);

        var stranger = new AppSettings { CloudProjectId = "demo-queueify", CloudQueueKey = CloudSync.NewQueueKey() };
        Assert.Equal(0, (await _cloud.SyncAsync(Store("d"), stranger, default)).Downloaded);
    }
}
