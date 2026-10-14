using Queueify.Launcher;

namespace Queueify.Tests;

// What the download's starter says when it can't start the app, in the Windows language.
public class StarterMessageTests
{
    [Fact]
    public void A_damaged_download_asks_to_download_again()
    {
        Assert.Equal("This copy of Queueify is damaged or incomplete. Download it again.",
            StarterMessage.For(new BundleDamagedException("hash"), spanish: false));
        Assert.Equal("Esta copia de Queueify está dañada o incompleta. Descargala de nuevo.",
            StarterMessage.For(new BundleDamagedException("hash"), spanish: true));
    }

    [Fact]
    public void Anything_else_says_what_happened()
    {
        Assert.Equal("Queueify couldn't start.\n\nDisk full.", StarterMessage.For(new IOException("Disk full."), spanish: false));
        Assert.Equal("Queueify no pudo arrancar.\n\nDisk full.", StarterMessage.For(new IOException("Disk full."), spanish: true));
    }

    [Fact]
    public void The_loading_screen_says_what_is_happening()
    {
        Assert.Equal("Getting Queueify ready…", StarterMessage.Loading(unpacking: true, spanish: false));
        Assert.Equal("Preparando Queueify…", StarterMessage.Loading(unpacking: true, spanish: true));
        Assert.Equal("Starting…", StarterMessage.Loading(unpacking: false, spanish: false));
        Assert.Equal("Abriendo…", StarterMessage.Loading(unpacking: false, spanish: true));
    }

    [Fact]
    public void The_loading_screen_shows_at_once_for_a_new_version_and_only_when_slow_otherwise()
    {
        // A version already unpacked opens in well under a second: no screen that flashes and goes.
        Assert.Equal(TimeSpan.Zero, StarterMessage.LoadingDelay(unpacking: true));
        Assert.Equal(TimeSpan.FromMilliseconds(600), StarterMessage.LoadingDelay(unpacking: false));
    }
}