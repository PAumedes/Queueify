using Queueify.Web.Services;

namespace Queueify.Web.Tests;

public sealed class PrivateFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("qw-private-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void On_unix_only_the_owner_can_read_or_write_the_file()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "File modes are a Unix feature.");
        var path = Path.Combine(_dir, "import.db");

        using (var file = PrivateFile.CreateNew(path)) file.WriteByte(1);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void An_existing_file_is_never_reused()
    {
        var path = Path.Combine(_dir, "import.db");
        File.WriteAllText(path, "planted");

        Assert.Throws<IOException>(() => PrivateFile.CreateNew(path));
        Assert.Equal("planted", File.ReadAllText(path));
    }
}
