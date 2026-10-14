using System.Text.Json;
using Queueify.Models;
using Queueify.Services;

namespace Queueify.Web.Tests.Sync;

/// <summary>The wire format of ADR-0010 with the real Core types, as a desktop client would use it.</summary>
public sealed class SyncContractTests(SyncFactory app) : IClassFixture<SyncFactory>
{
    private static SyncAlbum ToWire(BacklogItem i) => new(i.Uid, i.Artist, i.Album, i.Year, i.Source, i.CoverUrl, i.SpotifyId,
        i.TidalId, i.AddedAt.ToUniversalTime(), i.ListenedAt?.ToUniversalTime(), i.Rating, i.UpdatedAt, i.Deleted);

    private static BacklogItem FromWire(SyncAlbum a) => new()
    {
        Uid = a.Uid, Artist = a.Artist, Album = a.Album, Year = a.Year, Source = a.Source, CoverUrl = a.CoverUrl,
        SpotifyId = a.SpotifyId, TidalId = a.TidalId, AddedAt = a.AddedAt, ListenedAt = a.ListenedAt, Rating = a.Rating,
        UpdatedAt = a.UpdatedAt, Deleted = a.Deleted,
    };

    [Fact]
    public async Task A_PC_pushes_its_queue_and_another_PC_ends_up_with_the_same_albums()
    {
        var (_, token) = await app.NewUserAsync("contract");
        var dir = Path.Combine(Path.GetTempPath(), "qw-contract-" + Guid.NewGuid().ToString("N"));
        try
        {
            var pc1 = new BacklogStore(Path.Combine(dir, "pc1.db"));
            var pc2 = new BacklogStore(Path.Combine(dir, "pc2.db"));
            pc1.Insert(new BacklogItem { Artist = "Sonic Youth", Album = "Daydream Nation", Year = 1988, Source = "Rolling Stone", SpotifyId = "sp1", TidalId = "ti1", CoverUrl = "https://example.org/c.jpg", AddedAt = DateTime.UtcNow });
            pc1.Insert(new BacklogItem { Artist = "Luis Alberto Spinetta", Album = "Artaud", Year = 1973, AddedAt = DateTime.UtcNow, Rating = 4.5 });
            var gone = new BacklogItem { Artist = "Gone", Album = "Soon", AddedAt = DateTime.UtcNow };
            pc1.Insert(gone);
            pc1.Delete(gone);

            // PC 1 uploads what is dirty, the way CloudSync does
            var dirty = pc1.LoadDirty();
            var (status, up) = await app.SyncAsync(token, new SyncRequest(null, [.. dirty.Select(ToWire)], null));
            Assert.Equal(System.Net.HttpStatusCode.OK, status);
            foreach (var item in dirty) pc1.MarkUploaded(item);

            // PC 2 downloads
            var (_, down) = await app.SyncAsync(token, new SyncRequest(null, [], null));
            foreach (var album in down!.Albums) pc2.ApplyRemote(FromWire(album));

            Assert.Equal(pc1.LoadAll().OrderBy(i => i.Uid).Select(i => (i.Uid, i.Artist, i.Album, i.Year, i.Source, i.SpotifyId, i.TidalId, i.CoverUrl, i.Rating, i.UpdatedAt)),
                pc2.LoadAll().OrderBy(i => i.Uid).Select(i => (i.Uid, i.Artist, i.Album, i.Year, i.Source, i.SpotifyId, i.TidalId, i.CoverUrl, i.Rating, i.UpdatedAt)));
            Assert.Equal(2, pc2.LoadAll().Count);
            Assert.Empty(pc2.LoadDirty());
            Assert.Empty(up!.Albums);
            Assert.Empty((await app.SyncAsync(token, new SyncRequest(down.ServerTime, [], null))).Body!.Albums);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task The_documented_json_shape_is_what_goes_over_the_wire()
    {
        var (_, token) = await app.NewUserAsync("shape");
        using var response = await app.CreateClient().SendAsync(SyncFactory.Post(token, """{"since":null,"albums":[],"settings":null}"""));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(["serverTime", "albums", "settings"], json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.EndsWith("Z", json.RootElement.GetProperty("serverTime").GetString());
    }
}
