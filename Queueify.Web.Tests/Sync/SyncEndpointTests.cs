using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Queueify.Models;
using Queueify.Services;

namespace Queueify.Web.Tests.Sync;

public sealed class SyncEndpointTests(SyncFactory app) : IClassFixture<SyncFactory>
{
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private static SyncAlbum Album(string uid, DateTime? updated = null, string artist = "Artist", bool deleted = false) =>
        new(uid, artist, "Album " + uid, 1999, "Mag", null, null, null, T0, null, -1, updated ?? DateTime.UtcNow.AddMinutes(-5), deleted);

    private static SyncRequest Req(DateTime? since = null, params SyncAlbum[] albums) => new(since, [.. albums], null);

    // ── Authentication ──

    [Fact]
    public async Task No_token_is_401_with_an_empty_body()
    {
        using var response = await app.CreateClient().SendAsync(SyncFactory.Post(null, "{}"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("qw_" + "A234567890123456789012345678901234567890123")]
    [InlineData("not-a-token")]
    [InlineData("")]
    public async Task A_bad_token_is_401_with_an_empty_body(string token)
    {
        using var response = await app.CreateClient().SendAsync(SyncFactory.Post(token, "{}"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Basic_auth_with_a_valid_token_is_not_accepted()
    {
        var (_, token) = await app.NewUserAsync("basic");
        var request = SyncFactory.Post(null, "{}");
        request.Headers.TryAddWithoutValidation("Authorization", "Basic " + token);
        using var response = await app.CreateClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_revoked_token_is_401()
    {
        var (userId, token) = await app.NewUserAsync("revoked");
        Assert.Equal(HttpStatusCode.OK, (await app.SyncAsync(token, Req())).Status);

        using (var scope = app.Services.CreateScope())
        {
            var tokens = scope.ServiceProvider.GetRequiredService<Queueify.Web.Sync.PersonalAccessTokens>();
            var id = (await tokens.ListAsync(userId)).Single().Id;
            Assert.True(await tokens.RevokeAsync(userId, id));
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.SyncAsync(token, Req())).Status);
    }

    [Fact]
    public void The_endpoint_accepts_only_the_token_scheme_so_the_cookie_is_not_enough()
    {
        var endpoint = app.Services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints)
            .Single(e => e is RouteEndpoint r && r.RoutePattern.RawText == "/api/sync");
        var schemes = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.AuthenticationSchemes).ToList();
        var policy = app.Services.GetRequiredService<IAuthorizationPolicyProvider>()
            .GetPolicyAsync(endpoint.Metadata.GetMetadata<IAuthorizeData>()!.Policy!).Result!;
        Assert.Equal(["QueueifyToken"], policy.AuthenticationSchemes);
        Assert.NotEmpty(schemes);
    }

    // ── Basics and isolation ──

    [Fact]
    public async Task Answers_carry_secrets_so_they_are_never_cached()
    {
        var (_, token) = await app.NewUserAsync("nostore");
        using var response = await app.CreateClient().SendAsync(SyncFactory.Post(token, "{}"));
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task A_first_sync_with_nothing_answers_an_empty_response()
    {
        var (_, token) = await app.NewUserAsync("empty");
        var (status, body) = await app.SyncAsync(token, Req());
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Empty(body!.Albums);
        Assert.Null(body.Settings);
    }

    [Fact]
    public async Task An_album_pushed_from_one_PC_reaches_another_but_never_comes_back_to_the_sender()
    {
        var (_, token) = await app.NewUserAsync("two-pcs");
        var (_, first) = await app.SyncAsync(token, Req(null, Album("a1")));
        Assert.Empty(first!.Albums);

        var (_, second) = await app.SyncAsync(token, Req());
        Assert.Equal("a1", second!.Albums.Single().Uid);
        Assert.Equal("Album a1", second.Albums.Single().Album);

        var (_, third) = await app.SyncAsync(token, Req(second.ServerTime));
        Assert.Empty(third!.Albums);
    }

    [Fact]
    public async Task The_server_time_only_moves_forward()
    {
        var (_, token) = await app.NewUserAsync("monotonic");
        var times = new List<DateTime>();
        for (int i = 0; i < 5; i++) times.Add((await app.SyncAsync(token, Req(null, Album("m" + i)))).Body!.ServerTime);
        Assert.Equal(times.OrderBy(t => t), times);
        Assert.Equal(5, times.Distinct().Count());
    }

    [Fact]
    public async Task One_users_token_never_sees_or_changes_another_users_data()
    {
        var (_, alice) = await app.NewUserAsync("iso-a");
        var (_, bob) = await app.NewUserAsync("iso-b");
        await app.SyncAsync(alice, Req(null, Album("same", artist: "Alice's")));
        await app.SyncAsync(bob, Req(null, Album("same", artist: "Bob's"), Album("only-bob")));

        var (_, aliceView) = await app.SyncAsync(alice, Req());
        var (_, bobView) = await app.SyncAsync(bob, Req());

        Assert.Equal(["same"], aliceView!.Albums.Select(a => a.Uid));
        Assert.Equal("Alice's", aliceView.Albums.Single().Artist);
        Assert.Equal(["only-bob", "same"], bobView!.Albums.Select(a => a.Uid).Order());
        Assert.Equal("Bob's", bobView.Albums.Single(a => a.Uid == "same").Artist);
    }

    [Fact]
    public async Task Albums_added_on_the_web_side_reach_the_PCs()
    {
        var (userId, token) = await app.NewUserAsync("web-side");
        app.Stores.QueueOf(userId).Insert(new BacklogItem { Artist = "Phone", Album = "Added away from home", AddedAt = T0 });

        var (_, body) = await app.SyncAsync(token, Req());
        Assert.Equal("Phone", body!.Albums.Single().Artist);

        // a deletion made on the web side travels too
        var store = app.Stores.QueueOf(userId);
        store.Delete(store.LoadAll().Single());
        var (_, later) = await app.SyncAsync(token, Req(body.ServerTime));
        Assert.True(later!.Albums.Single().Deleted);
    }

    // ── Conflicts and deletes ──

    [Fact]
    public async Task The_newest_change_wins_and_an_older_one_is_answered_with_the_servers_row()
    {
        var (_, token) = await app.NewUserAsync("conflict");
        await app.SyncAsync(token, Req(null, Album("c", T0.AddHours(2), "Newer")));

        var (_, response) = await app.SyncAsync(token, Req(null, Album("c", T0.AddHours(1), "Older")));
        Assert.Equal("Newer", response!.Albums.Single().Artist);

        var (_, view) = await app.SyncAsync(token, Req());
        Assert.Equal("Newer", view!.Albums.Single().Artist);
    }

    [Fact]
    public async Task A_newer_change_replaces_an_older_one()
    {
        var (_, token) = await app.NewUserAsync("newer");
        await app.SyncAsync(token, Req(null, Album("n", T0, "Old")));
        await app.SyncAsync(token, Req(null, Album("n", T0.AddMinutes(1), "New")));
        Assert.Equal("New", (await app.SyncAsync(token, Req())).Body!.Albums.Single().Artist);
    }

    [Fact]
    public async Task On_a_tie_the_server_keeps_its_row()
    {
        var (_, token) = await app.NewUserAsync("tie");
        await app.SyncAsync(token, Req(null, Album("t", T0, "First")));
        var (_, answer) = await app.SyncAsync(token, Req(null, Album("t", T0, "Second")));

        Assert.Equal("First", answer!.Albums.Single().Artist);
        Assert.Equal("First", (await app.SyncAsync(token, Req())).Body!.Albums.Single().Artist);
    }

    [Fact]
    public async Task A_deletion_is_a_soft_row_other_PCs_receive_and_an_older_deletion_is_ignored()
    {
        var (_, token) = await app.NewUserAsync("deletes");
        await app.SyncAsync(token, Req(null, Album("d", T0.AddHours(1))));
        await app.SyncAsync(token, Req(null, Album("d", T0, deleted: true)));   // older: ignored
        Assert.False((await app.SyncAsync(token, Req())).Body!.Albums.Single().Deleted);

        await app.SyncAsync(token, Req(null, Album("d", T0.AddHours(2), deleted: true)));
        var (_, view) = await app.SyncAsync(token, Req());
        Assert.True(view!.Albums.Single().Deleted);

        await app.SyncAsync(token, Req(null, Album("d", T0.AddHours(3))));   // newer change brings it back
        Assert.False((await app.SyncAsync(token, Req())).Body!.Albums.Single().Deleted);
    }

    // ── Limits ──

    [Fact]
    public async Task More_than_a_thousand_albums_in_a_request_is_a_400()
    {
        var (_, token) = await app.NewUserAsync("too-many");
        var albums = Enumerable.Range(0, 1001).Select(i => Album("x" + i)).ToArray();
        Assert.Equal(HttpStatusCode.BadRequest, (await app.SyncAsync(token, Req(null, albums))).Status);
    }

    [Fact]
    public async Task A_response_holds_at_most_a_thousand_albums_and_the_client_can_resume()
    {
        var (_, uploader) = await app.NewUserAsync("page-up");
        for (int batch = 0; batch < 3; batch++)
            Assert.Equal(HttpStatusCode.OK, (await app.SyncAsync(uploader, Req(null,
                Enumerable.Range(0, batch == 2 ? 500 : 1000).Select(i => Album($"p{batch}-{i}")).ToArray()))).Status);

        var (_, puller) = (0, uploader);   // same user, another PC (no since)
        var seen = new HashSet<string>();
        DateTime? since = null;
        int pages = 0;
        while (true)
        {
            var (_, body) = await app.SyncAsync(puller, Req(since));
            Assert.InRange(body!.Albums.Count, 0, 1000);
            if (body.Albums.Count == 0) break;
            foreach (var a in body.Albums) seen.Add(a.Uid);
            since = body.ServerTime;
            Assert.InRange(++pages, 1, 10);
        }
        Assert.Equal(2500, seen.Count);
        Assert.True(pages >= 3);
    }

    [Fact]
    public async Task Text_is_cut_at_500_characters()
    {
        var (_, token) = await app.NewUserAsync("cut");
        var album = Album("long") with { Artist = new string('a', 900), Source = new string('b', 501), CoverUrl = "https://x/" + new string('c', 600) };
        await app.SyncAsync(token, Req(null, album));
        var stored = (await app.SyncAsync(token, Req())).Body!.Albums.Single();
        Assert.Equal(500, stored.Artist.Length);
        Assert.Equal(500, stored.Source!.Length);
        Assert.Equal(500, stored.CoverUrl!.Length);
    }

    [Fact]
    public async Task Unknown_fields_are_ignored()
    {
        var (_, token) = await app.NewUserAsync("unknown");
        const string json = """
            {"since":null,"future":{"a":1},"albums":[{"uid":"u1","artist":"A","album":"B","year":2000,"source":null,"coverUrl":null,
             "spotifyId":null,"tidalId":null,"addedAt":"2026-10-01T12:00:00Z","listenedAt":null,"rating":-1,
             "updatedAt":"2026-10-02T12:00:00Z","deleted":false,"extra":"x"}],"settings":null}
            """;
        using var response = await app.CreateClient().SendAsync(SyncFactory.Post(token, json));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("u1", (await app.SyncAsync(token, Req())).Body!.Albums.Single().Uid);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"albums":[{"uid":"../evil","artist":"a","album":"b","addedAt":"2026-10-01T12:00:00Z","updatedAt":"2026-10-01T12:00:00Z","rating":-1,"deleted":false}]}""")]
    [InlineData("""{"albums":[{"uid":"","artist":"a","album":"b","addedAt":"2026-10-01T12:00:00Z","updatedAt":"2026-10-01T12:00:00Z","rating":-1,"deleted":false}]}""")]
    public async Task Malformed_requests_are_a_400_with_an_empty_body(string json)
    {
        var (_, token) = await app.NewUserAsync("bad-json");
        using var response = await app.CreateClient().SendAsync(SyncFactory.Post(token, json));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_change_dated_far_in_the_future_cannot_win_forever()
    {
        var (_, token) = await app.NewUserAsync("future");
        await app.SyncAsync(token, Req(null, Album("f", DateTime.UtcNow.AddYears(50), "Poison")));
        await app.SyncAsync(token, Req(null, Album("f", DateTime.UtcNow.AddHours(1), "Fixed")));
        Assert.Equal("Fixed", (await app.SyncAsync(token, Req())).Body!.Albums.Single().Artist);
    }

    [Fact]
    public async Task Ratings_and_years_outside_their_range_are_made_valid()
    {
        var (_, token) = await app.NewUserAsync("range");
        await app.SyncAsync(token, Req(null, Album("r") with { Rating = 99, Year = -5 }));
        var stored = (await app.SyncAsync(token, Req())).Body!.Albums.Single();
        Assert.Equal(5, stored.Rating);
        Assert.Null(stored.Year);
    }

    // ── Settings ──

    [Fact]
    public async Task Settings_sync_newest_wins_and_the_secrets_are_not_plain_on_disk()
    {
        var (userId, token) = await app.NewUserAsync("settings");
        var mine = new SyncSettings("sp-id", "SPOTIFY-SECRET-VALUE-123", "ti-id", "TIDAL-SECRET-VALUE-456", "Spotify", "ES", T0.AddHours(2));
        await app.SyncAsync(token, new SyncRequest(null, [], mine));

        var older = mine with { SpotifyClientSecret = "OLD", UpdatedAt = T0 };
        var (_, answer) = await app.SyncAsync(token, new SyncRequest(null, [], older));
        Assert.Equal("SPOTIFY-SECRET-VALUE-123", answer!.Settings!.SpotifyClientSecret);   // rejected: the server's come back

        var (_, other) = await app.SyncAsync(token, Req());
        Assert.Equal(mine, other!.Settings);

        foreach (var file in Directory.EnumerateFiles(app.DataDir, "*", SearchOption.AllDirectories))
        {
            var text = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(file));
            Assert.DoesNotContain("SPOTIFY-SECRET-VALUE-123", text);
            Assert.DoesNotContain("TIDAL-SECRET-VALUE-456", text);
        }
        Assert.Equal("SPOTIFY-SECRET-VALUE-123", app.Stores.LoadSettings(userId).SpotifyClientSecret);
    }

    [Fact]
    public async Task Settings_saved_on_the_web_side_reach_the_PCs()
    {
        var (userId, token) = await app.NewUserAsync("web-settings");
        app.Stores.SaveSettings(userId, new AppSettings { CountryCode = "UY", SharedSettingsUpdatedAt = T0 });
        var (_, body) = await app.SyncAsync(token, Req());
        Assert.Equal("UY", body!.Settings!.CountryCode);
    }
}

public sealed class SyncLimitTests
{
    private sealed class Tiny() : SyncFactory(new() { ["Queueify:Sync:MaxBodyBytes"] = "2000" });
    private sealed class Throttled() : SyncFactory(new() { ["Queueify:Sync:RequestsPerMinute"] = "3" });

    [Fact]
    public async Task A_body_over_the_size_limit_is_413()
    {
        await using var app = new Tiny();
        var (_, token) = await app.NewUserAsync();
        using var response = await app.CreateClient().SendAsync(SyncFactory.Post(token, """{"albums":[],"pad":"%s"}""".Replace("%s", new string('x', 5000))));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Too_many_requests_in_a_minute_are_429()
    {
        await using var app = new Throttled();
        var (_, token) = await app.NewUserAsync();
        var statuses = new List<HttpStatusCode>();
        for (int i = 0; i < 5; i++) statuses.Add((await app.SyncAsync(token, new SyncRequest(null, [], null))).Status);
        Assert.Equal(HttpStatusCode.OK, statuses[0]);
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }

    [Fact]
    public async Task Guessing_tokens_is_throttled_too()
    {
        await using var app = new Throttled();
        var statuses = new List<HttpStatusCode>();
        for (int i = 0; i < 6; i++)
        {
            using var response = await app.CreateClient().SendAsync(SyncFactory.Post("qw_wrong" + i, "{}"));
            statuses.Add(response.StatusCode);
        }
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }
}
