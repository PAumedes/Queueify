using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Queueify.Models;
using Queueify.Services;
using Queueify.Web.Services;
using Queueify.Web.Storage;

namespace Queueify.Web.Tests.Settings;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qs-" + Guid.NewGuid().ToString("N"));
    private readonly UserStores _stores;
    public SettingsServiceTests() => _stores = new UserStores(_root, new EphemeralDataProtectionProvider().CreateProtector("t"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private sealed class FakeApi(HttpStatusCode token, HttpStatusCode search) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var isToken = request.RequestUri!.ToString().Contains("token");
            var status = isToken ? token : search;
            var body = isToken && status == HttpStatusCode.OK ? """{"access_token":"t","expires_in":3600}"""
                : """{"albums":{"items":[]}}""";
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private SettingsService Make(HttpStatusCode token = HttpStatusCode.OK, HttpStatusCode search = HttpStatusCode.OK) =>
        new(_stores, () => new HttpClient(new FakeApi(token, search)));

    [Fact]
    public void A_new_user_sees_defaults_and_no_keys()
    {
        var snap = Make().Snapshot("alice");
        Assert.False(snap.SpotifyConfigured);
        Assert.False(snap.TidalConfigured);
        Assert.Equal("AR", snap.CountryCode);
    }

    [Fact]
    public void The_snapshot_never_carries_a_secret()
    {
        _stores.SaveSettings("alice", new AppSettings { SpotifyClientId = "sid", SpotifyClientSecret = "TOPSECRET", TidalClientId = "tid", TidalClientSecret = "TIDALSECRET" });

        var snap = Make().Snapshot("alice");

        Assert.True(snap.SpotifyConfigured && snap.TidalConfigured);
        Assert.Equal("sid", snap.SpotifyClientId);
        var text = System.Text.Json.JsonSerializer.Serialize(snap);
        Assert.DoesNotContain("TOPSECRET", text);
        Assert.DoesNotContain("TIDALSECRET", text);
    }

    [Fact]
    public void Saving_a_new_secret_keeps_the_untouched_ones()
    {
        _stores.SaveSettings("alice", new AppSettings { SpotifyClientId = "sid", SpotifyClientSecret = "old", TidalClientId = "tid", TidalClientSecret = "tsec" });
        var service = Make();
        var opened = service.Snapshot("alice");

        var changed = service.Save("alice", opened, SettingsEdit.From(opened) with { SpotifyClientSecret = "new" });

        Assert.True(changed);
        var stored = _stores.LoadSettings("alice");
        Assert.Equal("new", stored.SpotifyClientSecret);
        Assert.Equal("tsec", stored.TidalClientSecret);
    }

    [Fact]
    public void Saving_with_nothing_edited_changes_nothing_and_stamps_nothing()
    {
        _stores.SaveSettings("alice", new AppSettings { SpotifyClientId = "sid", SpotifyClientSecret = "s" });
        var service = Make();
        var opened = service.Snapshot("alice");

        Assert.False(service.Save("alice", opened, SettingsEdit.From(opened)));
        Assert.Null(_stores.LoadSettings("alice").SharedSettingsUpdatedAt);
    }

    [Fact]
    public void A_change_made_elsewhere_while_the_page_was_open_survives_an_unrelated_save()
    {
        var service = Make();
        var opened = service.Snapshot("alice");
        _stores.SaveSettings("alice", new AppSettings { CountryCode = "ES" });   // a PC synced a newer country

        service.Save("alice", opened, SettingsEdit.From(opened) with { PreferredService = "Spotify" });

        var stored = _stores.LoadSettings("alice");
        Assert.Equal("ES", stored.CountryCode);
        Assert.Equal("Spotify", stored.PreferredService);
        Assert.NotNull(stored.SharedSettingsUpdatedAt);
    }

    [Theory]
    [InlineData("ZZ")]
    [InlineData("')--")]
    public void A_country_that_isnt_offered_is_refused(string code)
    {
        var service = Make();
        var opened = service.Snapshot("alice");
        Assert.Throws<ArgumentException>(() => service.Save("alice", opened, SettingsEdit.From(opened) with { CountryCode = code }));
    }

    [Fact]
    public void A_preferred_app_that_isnt_one_of_the_two_is_refused()
    {
        var service = Make();
        var opened = service.Snapshot("alice");
        Assert.Throws<ArgumentException>(() => service.Save("alice", opened, SettingsEdit.From(opened) with { PreferredService = "Napster" }));
    }

    [Fact]
    public void Absurdly_long_keys_are_refused()
    {
        var service = Make();
        var opened = service.Snapshot("alice");
        Assert.Throws<ArgumentException>(() => service.Save("alice", opened, SettingsEdit.From(opened) with { TidalClientSecret = new string('x', 5000) }));
    }

    [Fact]
    public void Pasted_keys_lose_their_stray_spaces()
    {
        var service = Make();
        var opened = service.Snapshot("alice");
        service.Save("alice", opened, SettingsEdit.From(opened) with { SpotifyClientId = " sid \n", SpotifyClientSecret = " sec " });
        var stored = _stores.LoadSettings("alice");
        Assert.Equal(("sid", "sec"), (stored.SpotifyClientId, stored.SpotifyClientSecret));
    }

    [Fact]
    public void Removing_a_services_keys_leaves_the_other_alone()
    {
        _stores.SaveSettings("alice", new AppSettings { SpotifyClientId = "s", SpotifyClientSecret = "s", TidalClientId = "t", TidalClientSecret = "t" });

        Make().RemoveKeys("alice", MusicService.Spotify);

        var stored = _stores.LoadSettings("alice");
        Assert.False(stored.SpotifyConfigured);
        Assert.True(stored.TidalConfigured);
    }

    [Fact]
    public async Task Testing_without_keys_says_to_save_them_first_and_calls_nothing()
    {
        var result = await Make().TestAsync("alice", MusicService.Tidal, TestContext.Current.CancellationToken);
        Assert.Equal(KeyTestOutcome.Missing, result.Outcome);
    }

    [Fact]
    public async Task Working_keys_are_reported_as_working()
    {
        _stores.SaveSettings("alice", new AppSettings { SpotifyClientId = "s", SpotifyClientSecret = "s" });
        var result = await Make().TestAsync("alice", MusicService.Spotify, TestContext.Current.CancellationToken);
        Assert.Equal(KeyTestOutcome.Ok, result.Outcome);
    }

    [Fact]
    public async Task Refused_keys_come_back_with_the_catalogs_own_actionable_text()
    {
        _stores.SaveSettings("alice", new AppSettings { SpotifyClientId = "s", SpotifyClientSecret = "bad" });
        var result = await Make(token: HttpStatusCode.Unauthorized).TestAsync("alice", MusicService.Spotify, TestContext.Current.CancellationToken);
        Assert.Equal(KeyTestOutcome.Rejected, result.Outcome);
        Assert.Contains("Spotify", result.Message);
        Assert.DoesNotContain("bad", result.Message);
    }

    [Fact]
    public async Task A_busy_service_is_not_blamed_on_the_keys()
    {
        _stores.SaveSettings("alice", new AppSettings { TidalClientId = "t", TidalClientSecret = "t" });
        var result = await Make(search: HttpStatusCode.ServiceUnavailable).TestAsync("alice", MusicService.Tidal, TestContext.Current.CancellationToken);
        Assert.Equal(KeyTestOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task A_network_failure_is_a_failure_not_a_crash()
    {
        _stores.SaveSettings("alice", new AppSettings { SpotifyClientId = "s", SpotifyClientSecret = "s" });
        var service = new SettingsService(_stores, () => new HttpClient(new Throwing()));
        var result = await service.TestAsync("alice", MusicService.Spotify, TestContext.Current.CancellationToken);
        Assert.Equal(KeyTestOutcome.Unexpected, result.Outcome);
    }

    private sealed class Throwing : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new HttpRequestException("dns");
    }
}
