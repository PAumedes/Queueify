using Queueify.Models;
using Queueify.Services;
using Queueify.Web.Services;

namespace Queueify.Web.Tests;

public class AlbumSearchTests
{
    private static AppSettings Keys(bool spotify = true, bool tidal = true) => new()
    {
        SpotifyClientId = spotify ? "id" : "", SpotifyClientSecret = spotify ? "secret" : "",
        TidalClientId = tidal ? "id" : "", TidalClientSecret = tidal ? "secret" : "",
    };

    private static AlbumMatch Match(MusicService s, string id, string title, string artist, int year) => new()
    {
        Service = s, Id = id, Title = title, Artists = [artist], Year = year,
    };

    [Fact]
    public async Task TheSameAlbumOnBothServices_IsOneRow_WithBothLinks()
    {
        var catalogs = new FakeCatalogs
        {
            Spotify = [Match(MusicService.Spotify, "sp1", "Clics Modernos", "Charly García", 1983)],
            Tidal = [Match(MusicService.Tidal, "td1", "Clics Modernos", "Charly García", 1983)],
        };

        var outcome = await new AlbumSearch(catalogs).RunAsync("charly garcia clics modernos 1983", "u", Keys(), default);

        var row = Assert.Single(outcome.Results);
        Assert.Equal("sp1", row.Spotify!.Id);
        Assert.Equal("td1", row.Tidal!.Id);
        Assert.True(outcome.Confident);
    }

    [Fact]
    public async Task OnlyTheConfiguredServicesAreAsked()
    {
        var catalogs = new FakeCatalogs { Spotify = [Match(MusicService.Spotify, "sp1", "Signos", "Soda Stereo", 1986)] };

        var outcome = await new AlbumSearch(catalogs).RunAsync("soda stereo signos", "u", Keys(tidal: false), default);

        Assert.Equal([MusicService.Spotify], catalogs.Asked.Select(a => a.Service).Distinct());
        Assert.Single(outcome.Results);
    }

    [Fact]
    public async Task NoKeysAtAll_MeansNothingIsAsked_AndTheOutcomeSaysSo()
    {
        var catalogs = new FakeCatalogs();
        var outcome = await new AlbumSearch(catalogs).RunAsync("anything", "u", Keys(false, false), default);

        Assert.Empty(catalogs.Asked);
        Assert.True(outcome.NeedsKeys);
    }

    [Fact]
    public async Task AServiceThatFails_IsReported_AndTheOtherStillAnswers()
    {
        var catalogs = new FakeCatalogs
        {
            Tidal = [Match(MusicService.Tidal, "td1", "Signos", "Soda Stereo", 1986)],
            SpotifyFails = new CatalogException("Spotify said no"),
        };

        var outcome = await new AlbumSearch(catalogs).RunAsync("soda stereo signos", "u", Keys(), default);

        Assert.Single(outcome.Results);
        Assert.Equal("Spotify: Spotify said no", Assert.Single(outcome.Errors));
    }

    [Fact]
    public async Task ANetworkError_BecomesAReadableMessage_NotACrash()
    {
        var catalogs = new FakeCatalogs { SpotifyFails = new HttpRequestException("boom") };
        var outcome = await new AlbumSearch(catalogs).RunAsync("x y z", "u", Keys(tidal: false), default);
        Assert.Contains("boom", Assert.Single(outcome.Errors));
    }

    [Fact]
    public async Task ANumberThatIsTheTitle_IsSearchedAgainWithTheYear()
    {
        var catalogs = new FakeCatalogs();
        catalogs.OnSearch = q => q == "Prince 1999"
            ? [Match(MusicService.Spotify, "sp9", "1999", "Prince", 1982)]
            : [];

        var outcome = await new AlbumSearch(catalogs).RunAsync("Prince 1999", "u", Keys(tidal: false), default);

        Assert.Contains("Prince", catalogs.Asked.Select(a => a.Query));
        Assert.Contains("Prince 1999", catalogs.Asked.Select(a => a.Query));
        Assert.Equal("sp9", outcome.Results.Single().Spotify!.Id);
    }

    [Fact]
    public async Task ACancelledSearch_ReturnsNull_SoANewerOneCanTakeOver()
    {
        using var cts = new CancellationTokenSource();
        var catalogs = new FakeCatalogs { OnSearch = _ => { cts.Cancel(); throw new OperationCanceledException(cts.Token); } };
        var outcome = await new AlbumSearch(catalogs).RunAsync("x y z", "u", Keys(tidal: false), cts.Token);
        Assert.Null(outcome);
    }

    [Fact]
    public async Task ATimeoutThatIsNotACancellation_IsReportedAsATimeout()
    {
        var catalogs = new FakeCatalogs { SpotifyFails = new TaskCanceledException() };
        var outcome = await new AlbumSearch(catalogs).RunAsync("x y z", "u", Keys(tidal: false), default);
        Assert.Contains("Spotify", Assert.Single(outcome!.Errors));
    }

    private sealed class FakeCatalogs : ICatalogs
    {
        public List<AlbumMatch> Spotify { get; init; } = [];
        public List<AlbumMatch> Tidal { get; init; } = [];
        public Exception? SpotifyFails { get; init; }
        public Func<string, List<AlbumMatch>>? OnSearch { get; set; }
        public List<(MusicService Service, string Query)> Asked { get; } = [];

        public Task<List<AlbumMatch>> SearchAsync(MusicService service, string query, string userId, AppSettings settings, CancellationToken ct)
        {
            Asked.Add((service, query));
            if (service == MusicService.Spotify && SpotifyFails != null) throw SpotifyFails;
            if (OnSearch != null) return Task.FromResult(OnSearch(query));
            return Task.FromResult(service == MusicService.Spotify ? Spotify : Tidal);
        }
    }
}
