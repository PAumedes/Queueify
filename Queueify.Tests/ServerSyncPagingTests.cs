using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Queueify.Models;
using Queueify.Services;

namespace Queueify.Tests;

/// <summary>Paging, validation and the size limit of <see cref="ServerSync"/>, against scripted answers.</summary>
public sealed class ServerSyncPagingTests : IDisposable
{
    private const string Token = "qw_SECRET-TOKEN-123";
    private const string Url = "https://queue.example.com";
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Edited = new(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queueify-tests", Guid.NewGuid().ToString("N"));
    private BacklogStore NewStore() => new(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db"));
    private static AppSettings Connected() => new() { WebServerUrl = Url, WebToken = Token };

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private sealed class Scripted(Func<int, SyncRequest, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public readonly List<SyncRequest> Requests = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            var req = JsonSerializer.Deserialize(body, SyncJson.Default.SyncRequest)!;
            Requests.Add(req);
            return answer(Requests.Count, req);
        }
    }

    private static HttpResponseMessage Ok(DateTime serverTime, List<SyncAlbum> albums) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new SyncResponse(serverTime, albums, null), SyncJson.Default.SyncResponse),
            Encoding.UTF8, "application/json"),
    };

    private static SyncAlbum Wire(string uid, string album = "Album", int? year = 1973, string? spotify = null, string? tidal = null,
        bool deleted = false, DateTime? updated = null) =>
        new(uid, "Spinetta", album, year, null, null, spotify, tidal, Edited, null, -1, updated ?? Edited, deleted);

    private static List<SyncAlbum> Tombstones(int count) =>
        Enumerable.Range(0, count).Select(i => Wire("gone" + i, deleted: true)).ToList();

    // ───────────── a full page means "maybe more" ─────────────

    [Fact]
    public async Task A_full_page_with_nothing_new_on_it_does_not_end_the_sync()
    {
        // A new PC meets 1000 old deletions first: nothing to apply, but the real albums are on the next page.
        var server = new Scripted((call, _) => call switch
        {
            1 => Ok(T0.AddSeconds(1), Tombstones(ServerSync.MaxBatch)),
            2 => Ok(T0.AddSeconds(2), [Wire("real", "The real album")]),
            _ => Ok(T0.AddSeconds(3), []),
        });
        var store = NewStore();

        var result = await new ServerSync(new HttpClient(server)).SyncAsync(store, Connected(), CancellationToken.None);

        Assert.Equal("The real album", Assert.Single(store.LoadAll()).Album);
        Assert.Equal(1, result.Downloaded);
        Assert.Equal(T0.AddSeconds(2), server.Requests[2].Since);
    }

    [Fact]
    public async Task A_full_page_that_does_not_move_the_position_ends_the_sync_instead_of_looping()
    {
        var server = new Scripted((_, _) => Ok(T0.AddSeconds(1), Tombstones(ServerSync.MaxBatch)));
        var settings = Connected();

        await new ServerSync(new HttpClient(server)).SyncAsync(NewStore(), settings, CancellationToken.None);

        Assert.Equal(2, server.Requests.Count);
        Assert.Equal(T0.AddSeconds(1), settings.WebLastServerTime);
    }

    [Fact]
    public async Task A_big_first_download_of_fifty_thousand_albums_is_not_cut_short_by_the_round_limit()
    {
        // 50 full pages is what the server's per-user quota allows.
        var server = new Scripted((call, _) => call <= 50
            ? Ok(T0.AddSeconds(call), Tombstones(ServerSync.MaxBatch))
            : Ok(T0.AddSeconds(51), []));

        await new ServerSync(new HttpClient(server)).SyncAsync(NewStore(), Connected(), CancellationToken.None);

        Assert.Equal(51, server.Requests.Count);
    }

    [Fact]
    public async Task By_default_a_request_carries_at_most_1000_albums()
    {
        var server = new Scripted((call, _) => Ok(T0.AddSeconds(call), []));
        var store = NewStore();
        foreach (var uid in Enumerable.Range(0, 1001))
            store.Insert(new BacklogItem { Uid = "u" + uid, Artist = "A", Album = "B", AddedAt = DateTime.Now });

        await new ServerSync(new HttpClient(server)).SyncAsync(store, Connected(), CancellationToken.None);

        Assert.Equal([1000, 1], server.Requests.Select(r => r.Albums.Count));
    }

    // ───────────── what is stored from an answer ─────────────

    [Theory]
    [InlineData(999, null)]
    [InlineData(1000, 1000)]
    [InlineData(2026, 2026)]
    [InlineData(3000, 3000)]
    [InlineData(3001, null)]
    [InlineData(0, null)]
    [InlineData(-5, null)]
    public async Task A_year_outside_the_one_shared_rule_is_dropped(int year, int? kept)
    {
        var server = new Scripted((_, _) => Ok(T0, [Wire("y", year: year)]));
        var store = NewStore();

        await new ServerSync(new HttpClient(server)).SyncAsync(store, Connected(), CancellationToken.None);

        Assert.Equal(kept, store.FindByUid("y")!.Year);
    }

    [Fact]
    public async Task A_service_id_that_is_not_a_plain_token_is_not_stored()
    {
        var server = new Scripted((_, _) => Ok(T0,
        [
            Wire("bad", spotify: "a b/../c", tidal: new string('9', 65)),
            Wire("good", spotify: "4aawyAB9vmqN3uQ7FjRGTy", tidal: "12345"),
        ]));
        var store = NewStore();

        await new ServerSync(new HttpClient(server)).SyncAsync(store, Connected(), CancellationToken.None);

        var bad = store.FindByUid("bad")!;
        Assert.Null(bad.SpotifyId);
        Assert.Null(bad.TidalId);
        Assert.Equal(("4aawyAB9vmqN3uQ7FjRGTy", "12345"), (store.FindByUid("good")!.SpotifyId, store.FindByUid("good")!.TidalId));
    }

    // ───────────── the answer size limit ─────────────

    private sealed class EndlessContent(long bytes) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var chunk = new byte[64 * 1024];
            Array.Fill(chunk, (byte)' ');
            for (long sent = 0; sent < bytes; sent += chunk.Length) await stream.WriteAsync(chunk);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;   // chunked: no Content-Length to check up front
        }
    }

    [Fact]
    public async Task An_answer_that_grows_past_the_limit_without_announcing_its_size_is_refused()
    {
        var server = new Scripted((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new EndlessContent(ServerSync.MaxAnswerBytes + 1024 * 1024),
        });

        var ex = await Assert.ThrowsAsync<SyncException>(() =>
            new ServerSync(new HttpClient(server)).SyncAsync(NewStore(), Connected(), CancellationToken.None));

        Assert.Equal(Loc.T("webSync.err.bad_reply"), ex.Message);
    }
}
