using Queueify.Models;
using Queueify.Web.Services;

namespace Queueify.Web.Tests;

public class SearchThrottleTests
{
    private sealed class Counting : ICatalogs
    {
        public int Calls;
        public Task<List<AlbumMatch>> SearchAsync(MusicService s, string q, string u, AppSettings st, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new List<AlbumMatch>());
        }
    }

    private static readonly AppSettings Keys = new() { SpotifyClientId = "i", SpotifyClientSecret = "s" };

    [Fact]
    public async Task One_user_cannot_flood_the_catalogs()
    {
        var catalogs = new Counting();
        var search = new AlbumSearch(catalogs);
        var user = Guid.NewGuid().ToString("N");

        var outcomes = new List<SearchOutcome?>();
        for (int i = 0; i < 100; i++) outcomes.Add(await search.RunAsync("some album " + i, user, Keys, default));

        Assert.True(catalogs.Calls <= 60, $"{catalogs.Calls} calls reached the catalogs");
        Assert.Contains(outcomes, o => o!.Throttled);
    }

    [Fact]
    public async Task Another_user_is_not_slowed_by_the_flood()
    {
        var search = new AlbumSearch(new Counting());
        var flooder = Guid.NewGuid().ToString("N");
        for (int i = 0; i < 100; i++) await search.RunAsync("x album " + i, flooder, Keys, default);

        Assert.False((await search.RunAsync("fresh album", Guid.NewGuid().ToString("N"), Keys, default))!.Throttled);
    }
}
