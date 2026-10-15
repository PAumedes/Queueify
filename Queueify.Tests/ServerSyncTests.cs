using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Queueify.Models;
using Queueify.Services;

namespace Queueify.Tests;

/// <summary>The desktop side of the web sync contract (ADR-0010), against an in-memory server.</summary>
public sealed class ServerSyncTests : IDisposable
{
    private const string Token = "qw_SECRET-TOKEN-123";
    private const string Url = "https://queue.example.com";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queueify-tests", Guid.NewGuid().ToString("N"));
    private BacklogStore NewStore() => new(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static AppSettings Connected() => new() { WebServerUrl = Url, WebToken = Token };

    private static BacklogItem Item(string album) => new()
    {
        Uid = Guid.NewGuid().ToString("N"), Artist = "Charly García", Album = album, Year = 1983,
        AddedAt = DateTime.Now,
    };

    private static SyncAlbum Wire(string uid, string album, DateTime updated, bool deleted = false) =>
        new(uid, "Spinetta", album, 1973, "Magazine", null, null, null, updated, null, -1, updated, deleted);

    /// <summary>A server that keeps rows by uid, stamps a rising serverTime and answers like ADR-0010.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        public readonly Dictionary<string, (SyncAlbum Album, DateTime ServerTime)> Rows = [];
        public SyncSettings? Settings;
        public readonly List<SyncRequest> Requests = [];
        public readonly List<string?> AuthHeaders = [];
        public readonly List<Uri?> Uris = [];
        public DateTime Clock = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        public Func<HttpRequestMessage, HttpResponseMessage?>? Override;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uris.Add(request.RequestUri);
            AuthHeaders.Add(request.Headers.Authorization?.ToString());
            if (Override?.Invoke(request) is { } custom) return custom;

            var body = await request.Content!.ReadAsStringAsync(ct);
            var req = JsonSerializer.Deserialize(body, SyncJson.Default.SyncRequest)!;
            Requests.Add(req);

            Clock = Clock.AddSeconds(1);
            var accepted = new HashSet<string>();
            foreach (var a in req.Albums)
            {
                if (Rows.TryGetValue(a.Uid, out var have) && have.Album.UpdatedAt >= a.UpdatedAt) continue; // ties keep the server's row
                Rows[a.Uid] = (a, Clock);
                accepted.Add(a.Uid);
            }
            SyncSettings? back = null;
            if (req.Settings is { } s && (Settings == null || s.UpdatedAt > Settings.UpdatedAt)) Settings = s;
            else if (req.Settings == null) back = Settings;

            var since = req.Since ?? DateTime.MinValue;
            var news = Rows.Values.Where(r => r.ServerTime > since && !accepted.Contains(r.Album.Uid)).Select(r => r.Album).ToList();
            var json = JsonSerializer.Serialize(new SyncResponse(Clock, news, back), SyncJson.Default.SyncResponse);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static ServerSync Client(FakeServer server) => new(new HttpClient(server));

    [Fact]
    public async Task Happy_path_uploads_dirty_albums_downloads_others_and_stores_since()
    {
        var server = new FakeServer();
        var theirs = Wire("remote1", "Artaud", new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc));
        server.Rows["remote1"] = (theirs, server.Clock);
        var store = NewStore();
        var mine = Item("Clics Modernos");
        store.Insert(mine);
        var settings = Connected();

        var result = await Client(server).SyncAsync(store, settings, CancellationToken.None);

        Assert.Equal(1, result.Uploaded);
        Assert.Equal(1, result.Downloaded);
        Assert.Contains(server.Rows.Values, r => r.Album.Album == "Clics Modernos");
        Assert.Contains(store.LoadAll(), i => i.Uid == "remote1" && i.Album == "Artaud");
        Assert.Empty(store.LoadDirty());
        Assert.Equal(server.Clock, settings.WebLastServerTime);
        Assert.Equal($"Bearer {Token}", server.AuthHeaders[0]);
        Assert.Equal(new Uri(Url + "/api/sync"), server.Uris[0]);
        Assert.Null(server.Requests[0].Since);
    }

    [Fact]
    public async Task The_second_sync_asks_only_for_what_is_newer()
    {
        var server = new FakeServer();
        var store = NewStore();
        var settings = Connected();
        await Client(server).SyncAsync(store, settings, CancellationToken.None);
        var first = settings.WebLastServerTime;

        await Client(server).SyncAsync(store, settings, CancellationToken.None);

        // A small overlap, in case the server commits two changes at nearly the same moment; applying a row twice is harmless.
        Assert.Equal(first - ServerSync.Overlap, server.Requests[^1].Since);
        Assert.Equal(2, server.Requests.Count);          // the overlap doesn't make an idle sync loop
    }

    [Fact]
    public async Task Later_rounds_of_one_sync_ask_from_the_exact_server_time_without_overlap()
    {
        var server = new FakeServer();
        var store = NewStore();
        var settings = Connected();
        settings.WebLastServerTime = server.Clock;
        for (int i = 0; i < 3; i++) store.Insert(Item("Album " + i));

        await new ServerSync(new HttpClient(server), maxBatch: 2).SyncAsync(store, settings, CancellationToken.None);

        var firstStart = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(firstStart - ServerSync.Overlap, server.Requests[0].Since);
        Assert.Equal(firstStart.AddSeconds(1), server.Requests[1].Since);   // the first answer's serverTime, exactly
    }

    // ───────────── what a server may send ─────────────

    private static HttpResponseMessage Answer(SyncResponse r) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(r, SyncJson.Default.SyncResponse), Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Downloaded_text_is_cut_the_uid_checked_and_the_year_bounded()
    {
        var t = BacklogStore.Now();
        var long1 = new string('x', 5000);
        var server = new FakeServer
        {
            Override = _ => Answer(new SyncResponse(t,
            [
                new SyncAlbum("ok1", long1, long1, 99999, long1, long1, long1, long1, t, null, 9, t, false),
                new SyncAlbum(new string('u', 65), "A", "B", 2000, null, null, null, null, t, null, -1, t, false),
                new SyncAlbum("", "A", "B", 2000, null, null, null, null, t, null, -1, t, false),
            ], null)),
        };
        var store = NewStore();

        await Client(server).SyncAsync(store, Connected(), CancellationToken.None);

        var got = Assert.Single(store.LoadAll());
        Assert.Equal("ok1", got.Uid);
        Assert.All(new[] { got.Artist, got.Album, got.Source!, got.CoverUrl! }, x => Assert.Equal(500, x.Length));
        Assert.Null(got.SpotifyId);   // a service id that is not a plain token is not kept
        Assert.Null(got.TidalId);
        Assert.Null(got.Year);
        Assert.Equal(5, got.Rating);
    }

    [Fact]
    public async Task A_far_future_change_time_is_pulled_back_so_it_cannot_win_forever()
    {
        var future = new DateTime(9999, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var server = new FakeServer
        {
            Override = _ => Answer(new SyncResponse(BacklogStore.Now(),
                [Wire("f1", "Future", future)],
                new SyncSettings("a", "b", "", "", "Tidal", "AR", future))),
        };
        var store = NewStore();
        var settings = Connected();

        await Client(server).SyncAsync(store, settings, CancellationToken.None);

        Assert.True(store.FindByUid("f1")!.UpdatedAt < BacklogStore.Now().AddMinutes(10));
        Assert.True(settings.SharedSettingsUpdatedAt < BacklogStore.Now().AddMinutes(10));
    }

    [Fact]
    public async Task Shared_settings_from_the_server_are_cut_to_500_characters()
    {
        var big = new string('k', 4000);
        var server = new FakeServer
        {
            Override = _ => Answer(new SyncResponse(BacklogStore.Now(), [], new SyncSettings(big, big, big, big, big, big, BacklogStore.Now()))),
        };
        var settings = Connected();

        await Client(server).SyncAsync(NewStore(), settings, CancellationToken.None);

        Assert.Equal(500, settings.SpotifyClientSecret.Length);
        Assert.Equal(500, settings.TidalClientId.Length);
    }

    [Fact]
    public async Task A_null_album_in_the_answer_is_a_bad_reply_not_a_crash()
    {
        var server = new FakeServer
        {
            Override = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"serverTime":"2026-10-14T12:00:00Z","albums":[null],"settings":null}""", Encoding.UTF8, "application/json"),
            },
        };

        var ex = await Assert.ThrowsAsync<SyncException>(() => Client(server).SyncAsync(NewStore(), Connected(), CancellationToken.None));

        Assert.Equal(Loc.T("webSync.err.bad_reply"), ex.Message);
    }

    [Theory]
    [InlineData("""{"albums":[]}""")]                                                   // no serverTime
    [InlineData("""{"serverTime":"9999-01-01T00:00:00Z","albums":[]}""")]              // absurd serverTime
    public async Task A_reply_without_a_usable_server_time_is_a_bad_reply_and_changes_nothing(string json)
    {
        var server = new FakeServer
        {
            Override = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") },
        };
        var settings = Connected();

        var ex = await Assert.ThrowsAsync<SyncException>(() => Client(server).SyncAsync(NewStore(), settings, CancellationToken.None));

        Assert.Equal(Loc.T("webSync.err.bad_reply"), ex.Message);
        Assert.Null(settings.WebLastServerTime);
    }

    [Fact]
    public async Task A_stored_sync_position_at_the_very_start_of_time_does_not_crash_the_next_sync()
    {
        var server = new FakeServer();
        var settings = Connected();
        settings.WebLastServerTime = DateTime.MinValue;

        await Client(server).SyncAsync(NewStore(), settings, CancellationToken.None);

        Assert.Null(server.Requests[0].Since);
    }

    private sealed class CutContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => throw new IOException("connection reset");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => throw new IOException("connection reset");
    }

    [Fact]
    public async Task A_connection_cut_while_the_answer_arrives_is_the_offline_message()
    {
        var server = new FakeServer { Override = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new CutContent() } };

        var ex = await Assert.ThrowsAsync<SyncException>(() => Client(server).SyncAsync(NewStore(), Connected(), CancellationToken.None));

        Assert.Equal(Loc.T("webSync.err.offline"), ex.Message);
    }

    [Fact]
    public async Task An_oversized_answer_is_refused_without_reading_it()
    {
        var server = new FakeServer
        {
            Override = _ =>
            {
                var content = new StringContent("{}");
                content.Headers.ContentLength = ServerSync.MaxAnswerBytes + 1;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            },
        };

        var ex = await Assert.ThrowsAsync<SyncException>(() => Client(server).SyncAsync(NewStore(), Connected(), CancellationToken.None));

        Assert.Equal(Loc.T("webSync.err.bad_reply"), ex.Message);
    }

    [Fact]
    public async Task The_apps_own_client_does_not_follow_a_redirect_to_another_server()
    {
        using var first = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        using var second = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        first.Start();
        second.Start();
        int secondPort = ((IPEndPoint)second.LocalEndpoint).Port;
        int firstPort = ((IPEndPoint)first.LocalEndpoint).Port;
        var serving = Task.Run(async () =>
        {
            using var c = await first.AcceptTcpClientAsync();
            var buffer = new byte[4096];
            await c.GetStream().ReadAsync(buffer);
            var reply = Encoding.ASCII.GetBytes($"HTTP/1.1 307 Temporary Redirect\r\nLocation: http://127.0.0.1:{secondPort}/api/sync\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await c.GetStream().WriteAsync(reply);
        });
        using var http = ServerSync.CreateClient();
        var settings = new AppSettings { WebServerUrl = $"http://127.0.0.1:{firstPort}", WebToken = Token };

        var ex = await Assert.ThrowsAsync<SyncException>(() => new ServerSync(http).SyncAsync(NewStore(), settings, CancellationToken.None));
        await serving;

        Assert.Equal(Loc.T("webSync.err.redirect"), ex.Message);
        Assert.False(second.Pending());
    }

    [Fact]
    public async Task The_newest_change_wins_when_both_sides_edited_the_same_album()
    {
        var server = new FakeServer();
        var store = NewStore();
        var item = Item("Edited here");
        store.Insert(item);                                   // updated "now"
        server.Rows[item.Uid] = (Wire(item.Uid, "Server title", BacklogStore.Now().AddHours(1)), server.Clock);

        await Client(server).SyncAsync(store, Connected(), CancellationToken.None);

        Assert.Equal("Server title", store.FindByUid(item.Uid)!.Album);
        Assert.Equal("Server title", server.Rows[item.Uid].Album.Album);
    }

    [Fact]
    public async Task A_newer_local_edit_overwrites_the_servers_row()
    {
        var server = new FakeServer();
        var store = NewStore();
        var item = Item("New title");
        store.Insert(item);
        server.Rows[item.Uid] = (Wire(item.Uid, "Old title", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)), server.Clock);

        await Client(server).SyncAsync(store, Connected(), CancellationToken.None);

        Assert.Equal("New title", server.Rows[item.Uid].Album.Album);
        Assert.Equal("New title", store.FindByUid(item.Uid)!.Album);
    }

    [Fact]
    public async Task Deletions_go_up_as_soft_rows_and_come_down_as_removals()
    {
        var server = new FakeServer();
        var store = NewStore();
        var gone = Item("Gone here");
        var goneThere = Item("Gone there");
        store.Insert(gone);
        store.Insert(goneThere);
        store.MarkUploaded(goneThere);
        store.Delete(gone);
        server.Rows[goneThere.Uid] = (Wire(goneThere.Uid, "Gone there", BacklogStore.Now().AddHours(1), deleted: true), server.Clock);

        await Client(server).SyncAsync(store, Connected(), CancellationToken.None);

        Assert.True(server.Rows[gone.Uid].Album.Deleted);
        Assert.Null(store.FindByUid(gone.Uid));          // the uploaded deletion is removed for good
        Assert.Null(store.FindByUid(goneThere.Uid));     // the other PC's deletion applied
        Assert.Empty(store.LoadAll());
    }

    [Fact]
    public async Task Repeats_until_the_server_has_nothing_more_and_sends_at_most_1000_albums_per_request()
    {
        var server = new FakeServer();
        var store = NewStore();
        for (int i = 0; i < 5; i++) store.Insert(Item("Album " + i));
        var settings = Connected();

        var result = await new ServerSync(new HttpClient(server), maxBatch: 2).SyncAsync(store, settings, CancellationToken.None);

        Assert.Equal(5, result.Uploaded);
        Assert.Equal(5, server.Rows.Count);
        Assert.Equal([2, 2, 1], server.Requests.Select(r => r.Albums.Count));
        Assert.Empty(store.LoadDirty());
    }

    [Fact]
    public void The_default_batch_is_the_contracts_1000()
    {
        Assert.Equal(1000, ServerSync.MaxBatch);
    }

    [Fact]
    public async Task Downloads_that_arrive_in_several_rounds_are_all_applied()
    {
        var server = new FakeServer();
        var calls = 0;
        server.Override = _ =>
        {
            calls++;
            if (calls > 2) return null;
            var album = Wire("page" + calls, "Page " + calls, new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc));
            var json = JsonSerializer.Serialize(new SyncResponse(new DateTime(2026, 10, 1, 12, 0, calls, DateTimeKind.Utc), [album], null), SyncJson.Default.SyncResponse);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        };
        var store = NewStore();

        var result = await Client(server).SyncAsync(store, Connected(), CancellationToken.None);

        Assert.Equal(2, result.Downloaded);
        Assert.Equal(["Page 1", "Page 2"], store.LoadAll().Select(i => i.Album).Order());
    }

    [Fact]
    public async Task A_server_that_never_stops_answering_with_albums_ends_with_an_error()
    {
        var server = new FakeServer();
        int n = 0;
        server.Override = _ =>
        {
            var album = Wire("loop" + n++, "Loop", new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc));
            var json = JsonSerializer.Serialize(new SyncResponse(new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc), [album], null), SyncJson.Default.SyncResponse);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        };

        await Assert.ThrowsAsync<SyncException>(() => Client(server).SyncAsync(NewStore(), Connected(), CancellationToken.None));
    }

    [Fact]
    public async Task A_rejected_token_is_a_clear_message_and_changes_nothing()
    {
        var server = new FakeServer { Override = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) };
        var store = NewStore();
        var item = Item("Pending");
        store.Insert(item);
        var settings = Connected();

        var ex = await Assert.ThrowsAsync<SyncException>(() => Client(server).SyncAsync(store, settings, CancellationToken.None));

        Assert.Equal(Loc.T("webSync.err.unauthorized"), ex.Message);
        Assert.Single(store.LoadDirty());
        Assert.Null(settings.WebLastServerTime);
    }

    [Fact]
    public async Task A_slow_server_times_out_after_twenty_seconds_with_a_sync_exception()
    {
        var server = new FakeServer
        {
            Override = r => throw new TaskCanceledException("simulated client timeout", new TimeoutException()),
        };

        var ex = await Assert.ThrowsAsync<SyncException>(() => Client(server).SyncAsync(NewStore(), Connected(), CancellationToken.None));

        Assert.Equal(Loc.T("webSync.err.timeout"), ex.Message);
        Assert.Equal(TimeSpan.FromSeconds(20), ServerSync.Timeout);
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_reported_as_a_timeout()
    {
        using var cts = new CancellationTokenSource();
        var server = new FakeServer
        {
            Override = r => { cts.Cancel(); throw new OperationCanceledException(cts.Token); },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(server).SyncAsync(NewStore(), Connected(), cts.Token));
    }

    [Theory]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    public async Task A_redirect_is_refused_and_never_followed(HttpStatusCode status)
    {
        var server = new FakeServer
        {
            Override = _ =>
            {
                var res = new HttpResponseMessage(status);
                res.Headers.Location = new Uri("https://evil.example.net/api/sync");
                return res;
            },
        };

        var ex = await Assert.ThrowsAsync<SyncException>(() => Client(server).SyncAsync(NewStore(), Connected(), CancellationToken.None));

        Assert.Equal(Loc.T("webSync.err.redirect"), ex.Message);
        Assert.Single(server.Uris);
    }

    [Fact]
    public async Task The_token_is_never_in_an_error_message()
    {
        string[] bodies = [$"oops {Token}", "{}", "not json"];
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.InternalServerError, HttpStatusCode.BadRequest, HttpStatusCode.OK })
        foreach (var body in bodies)
        {
            var server = new FakeServer { Override = _ => new HttpResponseMessage(status) { Content = new StringContent(body) } };
            try { await Client(server).SyncAsync(NewStore(), Connected(), CancellationToken.None); }
            catch (SyncException ex) { Assert.DoesNotContain(Token, ex.ToString()); Assert.DoesNotContain(Token, ex.Message); }
        }

        var failing = new FakeServer { Override = _ => throw new HttpRequestException($"connect failed for {Token}") };
        var offline = await Assert.ThrowsAsync<SyncException>(() => Client(failing).SyncAsync(NewStore(), Connected(), CancellationToken.None));
        Assert.DoesNotContain(Token, offline.ToString());
        Assert.Null(offline.InnerException);
    }

    [Theory]
    [InlineData("http://queue.example.com")]      // plain http to a remote host
    [InlineData("ftp://queue.example.com")]
    [InlineData("")]
    public async Task Only_https_servers_are_used_nothing_is_sent_otherwise(string url)
    {
        var server = new FakeServer();

        await Assert.ThrowsAsync<SyncException>(() =>
            Client(server).SyncAsync(NewStore(), new AppSettings { WebServerUrl = url, WebToken = Token }, CancellationToken.None));

        Assert.Empty(server.Uris);
    }

    [Fact]
    public async Task Loopback_http_is_allowed_for_local_testing()
    {
        var server = new FakeServer();

        await Client(server).SyncAsync(NewStore(), new AppSettings { WebServerUrl = "http://127.0.0.1:5000", WebToken = Token }, CancellationToken.None);

        Assert.Equal(new Uri("http://127.0.0.1:5000/api/sync"), server.Uris.Single());
    }

    [Fact]
    public async Task Long_text_is_cut_at_500_characters_on_upload()
    {
        var server = new FakeServer();
        var store = NewStore();
        store.Insert(Item(new string('x', 900)));

        await Client(server).SyncAsync(store, Connected(), CancellationToken.None);

        Assert.Equal(500, server.Rows.Values.Single().Album.Album.Length);
    }

    [Fact]
    public async Task Albums_keep_every_field_through_a_round_trip_between_two_pcs()
    {
        var server = new FakeServer();
        var pcA = NewStore();
        var pcB = NewStore();
        var item = new BacklogItem
        {
            Artist = "Spinetta", Album = "Artaud", Year = 1973, Source = "Rolling Stone #3", CoverUrl = "https://c/1.jpg",
            SpotifyId = "sp1", TidalId = "ti1", AddedAt = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Local),
            ListenedAt = new DateTime(2026, 9, 20, 22, 30, 0, DateTimeKind.Local), Rating = 4.5,
        };
        pcA.Insert(item);

        await Client(server).SyncAsync(pcA, Connected(), CancellationToken.None);
        await Client(server).SyncAsync(pcB, Connected(), CancellationToken.None);

        var got = pcB.FindByUid(item.Uid)!;
        Assert.Equal(("Spinetta", "Artaud", 1973, "Rolling Stone #3", "https://c/1.jpg"), (got.Artist, got.Album, got.Year, got.Source, got.CoverUrl));
        Assert.Equal(("sp1", "ti1", 4.5), (got.SpotifyId, got.TidalId, got.Rating));
        Assert.Equal(item.AddedAt.ToUniversalTime(), got.AddedAt.ToUniversalTime());
        Assert.Equal(item.ListenedAt!.Value.ToUniversalTime(), got.ListenedAt!.Value.ToUniversalTime());
        Assert.False(got.Dirty);
    }

    // ───────────── settings ─────────────

    [Fact]
    public async Task Changed_shared_settings_go_up_once_and_other_fields_never_do()
    {
        var server = new FakeServer();
        var settings = Connected();
        settings.SpotifyClientId = "sid";
        settings.SpotifyClientSecret = "ssecret";
        settings.CountryCode = "ES";
        settings.SharedSettingsUpdatedAt = new DateTime(2026, 10, 1, 11, 0, 0, DateTimeKind.Utc);
        settings.LastSource = "private note";

        await Client(server).SyncAsync(NewStore(), settings, CancellationToken.None);
        await Client(server).SyncAsync(NewStore(), settings, CancellationToken.None);

        Assert.Equal("sid", server.Requests[0].Settings!.SpotifyClientId);
        Assert.Equal("ES", server.Requests[0].Settings!.CountryCode);
        Assert.All(server.Requests.Skip(1), r => Assert.Null(r.Settings));
        var wire = JsonSerializer.Serialize(server.Requests[0], SyncJson.Default.SyncRequest);
        Assert.DoesNotContain(Token, wire);
        Assert.DoesNotContain("private note", wire);
    }

    [Fact]
    public async Task Newer_settings_from_the_server_are_applied_and_reported()
    {
        var server = new FakeServer
        {
            Settings = new SyncSettings("a", "b", "c", "d", "Spotify", "UY", new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc)),
        };
        var settings = Connected();

        var result = await Client(server).SyncAsync(NewStore(), settings, CancellationToken.None);

        Assert.True(result.SettingsChanged);
        Assert.Equal(("a", "b", "c", "d", "Spotify", "UY"),
            (settings.SpotifyClientId, settings.SpotifyClientSecret, settings.TidalClientId, settings.TidalClientSecret, settings.PreferredService, settings.CountryCode));
        Assert.Equal(new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc), settings.SharedSettingsUpdatedAt);
        Assert.Equal(Token, settings.WebToken);
    }

    [Fact]
    public async Task Older_settings_from_the_server_do_not_replace_newer_ones_here()
    {
        var server = new FakeServer
        {
            Settings = new SyncSettings("old", "old", "", "", "Tidal", "AR", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
        };
        var settings = Connected();
        settings.SpotifyClientId = "mine";
        settings.SharedSettingsUpdatedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        settings.WebSettingsSyncedAt = settings.SharedSettingsUpdatedAt;   // nothing new to send

        var result = await Client(server).SyncAsync(NewStore(), settings, CancellationToken.None);

        Assert.False(result.SettingsChanged);
        Assert.Equal("mine", settings.SpotifyClientId);
    }

    [Fact]
    public async Task A_first_connection_with_keys_and_an_empty_server_uploads_the_keys()
    {
        var server = new FakeServer();
        var settings = Connected();
        settings.TidalClientId = "tid";
        settings.TidalClientSecret = "tsec";

        await Client(server).SyncAsync(NewStore(), settings, CancellationToken.None);

        Assert.Equal("tid", server.Settings!.TidalClientId);
        Assert.NotNull(settings.SharedSettingsUpdatedAt);
    }

    [Fact]
    public async Task A_first_connection_takes_the_servers_keys_instead_of_uploading_its_own()
    {
        var server = new FakeServer
        {
            Settings = new SyncSettings("srv", "srv", "", "", "Tidal", "AR", new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc)),
        };
        var settings = Connected();
        settings.SpotifyClientId = "local";
        settings.SpotifyClientSecret = "local";

        await Client(server).SyncAsync(NewStore(), settings, CancellationToken.None);

        Assert.Equal("srv", server.Settings!.SpotifyClientId);
        Assert.Equal("srv", settings.SpotifyClientId);
    }

    [Fact]
    public void The_connection_belongs_to_this_pc_and_is_not_shared_with_the_others()
    {
        var mine = new AppSettings { WebServerUrl = Url, WebToken = Token, WebLastServerTime = DateTime.UtcNow };
        var synced = new AppSettings { SpotifyClientId = "s", SharedSettingsUpdatedAt = DateTime.UtcNow, WebToken = "other" };

        mine.CopySharedFrom(synced);
        var edited = mine.Clone();
        edited.WebToken = "changed";
        var changed = mine.ApplyEdits(mine.Clone(), edited);

        Assert.Equal(Token, mine.WebToken);
        Assert.Equal(Url, mine.WebServerUrl);
        Assert.False(changed);
        Assert.NotNull(mine.WebLastServerTime);
        Assert.Equal("changed", edited.Clone().WebToken);   // Clone keeps the connection
    }

    [Fact]
    public void Settings_round_trip_through_settings_json_with_the_token_in_one_place_only()
    {
        var path = Path.Combine(_dir, "settings.json");
        Directory.CreateDirectory(_dir);
        var s = Connected();
        s.WebLastServerTime = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        s.SpotifyClientId = "sid";

        AppData.SaveSettings(s, path);
        var text = File.ReadAllText(path);
        var back = AppData.LoadSettings(path);

        Assert.Equal(1, text.Split(Token).Length - 1);
        Assert.Equal((Url, Token, s.WebLastServerTime), (back.WebServerUrl, back.WebToken, back.WebLastServerTime));
        Assert.True(back.WebConfigured);
        Assert.False(back.CloudConfigured);
    }

    [Fact]
    public void Web_connection_needs_both_the_server_and_the_token()
    {
        Assert.False(new AppSettings { WebServerUrl = Url }.WebConfigured);
        Assert.False(new AppSettings { WebToken = Token }.WebConfigured);
        Assert.True(new AppSettings { WebServerUrl = Url, WebToken = Token }.WebConfigured);
    }

    [Fact]
    public void Connecting_to_one_server_replaces_the_other_provider_and_web_wins_if_both_are_set()
    {
        var both = new AppSettings
        {
            CloudProjectId = "queueify-1a2b3", CloudQueueKey = "k", CloudLastServerTime = DateTime.UtcNow,
            WebServerUrl = Url, WebToken = Token,
        };

        Assert.True(both.SyncConfigured);
        Assert.Equal(SyncKind.Web, both.ActiveSync);
        Assert.Equal(SyncKind.Cloud, new AppSettings { CloudProjectId = "queueify-1a2b3", CloudQueueKey = "k" }.ActiveSync);
        Assert.Equal(SyncKind.None, new AppSettings().ActiveSync);

        both.ConnectWeb(Url, Token);
        Assert.False(both.CloudConfigured);
        Assert.Null(both.CloudLastServerTime);
        both.ConnectCloud("queueify-1a2b3", "k2");
        Assert.False(both.WebConfigured);
        Assert.Null(both.WebLastServerTime);
        Assert.Equal("", both.WebToken);
    }

    [Fact]
    public void Disconnecting_forgets_the_token_and_the_sync_position()
    {
        var s = Connected();
        s.WebLastServerTime = DateTime.UtcNow;
        s.WebSettingsSyncedAt = DateTime.UtcNow;

        s.DisconnectWeb();

        Assert.Equal(("", "", null, null), (s.WebServerUrl, s.WebToken, s.WebLastServerTime, s.WebSettingsSyncedAt));
    }

    [Fact]
    public void The_providers_share_one_interface()
    {
        Assert.IsAssignableFrom<ISyncProvider>(new ServerSync(new HttpClient()));
        Assert.IsAssignableFrom<ISyncProvider>(new CloudSync(new HttpClient()));
        Assert.IsType<ServerSync>(SyncProviders.For(SyncKind.Web, new CloudSync(new HttpClient()), new ServerSync(new HttpClient())));
    }
}
