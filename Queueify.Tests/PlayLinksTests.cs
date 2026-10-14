using Queueify.Models;
using Queueify.Services;

namespace Queueify.Tests;

/// <summary>The links Play opens: the installed app first, the web player as the fallback.</summary>
public class PlayLinksTests
{
    [Fact]
    public void Spotify_HasAnAppLinkAndAWebFallback()
    {
        var link = PlayLinks.For(MusicService.Spotify, "4aawyAB9vmqN3uQ7FjRGTy");
        Assert.Equal("spotify:album:4aawyAB9vmqN3uQ7FjRGTy", link.App);
        Assert.Equal("https://open.spotify.com/album/4aawyAB9vmqN3uQ7FjRGTy", link.Web);
    }

    [Fact]
    public void Tidal_HasAnAppLinkAndAWebFallback()
    {
        var link = PlayLinks.For(MusicService.Tidal, "12345");
        Assert.Equal("tidal://album/12345", link.App);
        Assert.Equal("https://tidal.com/album/12345", link.Web);
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("x?y=1")]
    [InlineData("a b")]
    [InlineData("")]
    public void AnIdThatIsNotAPlainToken_IsRefused(string id) =>
        Assert.Null(PlayLinks.Of(new BacklogItem { SpotifyId = id }, MusicService.Spotify));

    [Fact]
    public void Of_UsesThePreferredServiceWhenTheAlbumIsOnBoth()
    {
        var item = new BacklogItem { SpotifyId = "sp", TidalId = "td" };
        Assert.Equal("https://tidal.com/album/td", PlayLinks.Of(item, MusicService.Tidal)!.Web);
        Assert.Equal("https://open.spotify.com/album/sp", PlayLinks.Of(item, MusicService.Spotify)!.Web);
    }

    [Fact]
    public void Of_FallsBackToTheOnlyServiceThereIs()
    {
        Assert.Equal("https://tidal.com/album/td", PlayLinks.Of(new BacklogItem { TidalId = "td" }, MusicService.Spotify)!.Web);
        Assert.Null(PlayLinks.Of(new BacklogItem(), MusicService.Tidal));
    }
}
