using Queueify.Services;

namespace Queueify.Web.Tests.Sync;

/// <summary>An album year is a real release year: 1000 to 3000. Anything else is stored as "no year".</summary>
public sealed class YearRangeTests(SyncFactory app) : IClassFixture<SyncFactory>
{
    private static SyncAlbum Album(string uid, int year) =>
        new(uid, "Artist", "Album " + uid, year, null, null, null, null, DateTime.UtcNow.AddDays(-1), null, -1, DateTime.UtcNow.AddMinutes(-5), false);

    [Theory]
    [InlineData(999, null)]
    [InlineData(1000, 1000)]
    [InlineData(1986, 1986)]
    [InlineData(3000, 3000)]
    [InlineData(3001, null)]
    [InlineData(9999, null)]
    public async Task The_server_keeps_only_years_in_range(int sent, int? kept)
    {
        var (_, token) = await app.NewUserAsync("year");
        await app.SyncAsync(token, new SyncRequest(null, [Album("y", sent)], null));

        var stored = (await app.SyncAsync(token, new SyncRequest(null, [], null))).Body!.Albums.Single();

        Assert.Equal(kept, stored.Year);
    }
}
