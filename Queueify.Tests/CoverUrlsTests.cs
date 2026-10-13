using Queueify.Services;

namespace Queueify.Tests;

// Cover addresses come from Spotify, Tidal and other PCs through sync: only web addresses are loaded.
public class CoverUrlsTests
{
    [Theory]
    [InlineData("https://i.scdn.co/image/ab67616d0000b273")]
    [InlineData("http://resources.tidal.com/images/a/b/c/640x640.jpg")]
    public void Web_addresses_are_loaded(string url)
    {
        Assert.Equal(new Uri(url), CoverUrls.Loadable(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("/relative/cover.jpg")]
    [InlineData(@"C:\Users\someone\cover.jpg")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData(@"\\attacker\share\cover.jpg")]
    [InlineData("ms-appx:///Assets/Queueify-256.png")]
    public void Anything_else_is_not(string? url)
    {
        Assert.Null(CoverUrls.Loadable(url));
    }
}
