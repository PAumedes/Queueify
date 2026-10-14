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
}
