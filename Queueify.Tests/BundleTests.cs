using Queueify.Launcher;

namespace Queueify.Tests;

// The download is a small starter exe with the app appended to it, compressed; the starter unpacks it
// once per version into a cache folder and runs it from there.
public sealed class BundleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queueify-tests", Guid.NewGuid().ToString("N"));

    public BundleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string App(string name, string exeText = "the app")
    {
        var app = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.Combine(app, "Assets"));
        File.WriteAllText(Path.Combine(app, "Queueify.exe"), exeText);
        File.WriteAllText(Path.Combine(app, "Assets", "Queueify.ico"), "icon");
        File.WriteAllBytes(Path.Combine(app, "resources.pri"), [0, 1, 2, 255]);
        return app;
    }

    private string Starter()
    {
        var starter = Path.Combine(_dir, "starter.exe");
        File.WriteAllText(starter, "MZ starter code");
        return starter;
    }

    private BundleInfo Bundled(string app, string name = "Queueify-1.3.0-win-x64.exe")
    {
        var output = Path.Combine(_dir, name);
        Bundle.Write(Starter(), app, output);
        using var exe = File.OpenRead(output);
        return Bundle.Read(exe) ?? throw new InvalidOperationException("no bundle found");
    }

    [Fact]
    public void The_app_comes_back_out_exactly_as_it_went_in()
    {
        var output = Path.Combine(_dir, "Queueify.exe");
        Bundle.Write(Starter(), App("app"), output);

        string unpacked;
        using (var exe = File.OpenRead(output))
            unpacked = Bundle.Unpack(exe, Bundle.Read(exe)!, Path.Combine(_dir, "cache"));

        Assert.Equal("the app", File.ReadAllText(Path.Combine(unpacked, "Queueify.exe")));
        Assert.Equal("icon", File.ReadAllText(Path.Combine(unpacked, "Assets", "Queueify.ico")));
        Assert.Equal(new byte[] { 0, 1, 2, 255 }, File.ReadAllBytes(Path.Combine(unpacked, "resources.pri")));
    }

    [Fact]
    public void The_starter_still_runs_as_it_was_built()
    {
        var output = Path.Combine(_dir, "Queueify.exe");
        Bundle.Write(Starter(), App("app"), output);

        Assert.StartsWith("MZ starter code", File.ReadAllText(output, System.Text.Encoding.Latin1));
    }

    [Fact]
    public void An_exe_without_an_app_appended_has_no_bundle()
    {
        using var exe = File.OpenRead(Starter());
        Assert.Null(Bundle.Read(exe));
    }

    [Fact]
    public void Each_version_unpacks_to_its_own_folder()
    {
        Assert.NotEqual(Bundled(App("one", "1.3.0")).Id, Bundled(App("two", "1.3.1")).Id);
        Assert.Equal(Bundled(App("same", "1.3.0")).Id, Bundled(App("again", "1.3.0")).Id);
    }

    [Fact]
    public void A_version_already_unpacked_is_reused_not_unpacked_again()
    {
        var output = Path.Combine(_dir, "Queueify.exe");
        Bundle.Write(Starter(), App("app"), output);
        var cache = Path.Combine(_dir, "cache");
        using var exe = File.OpenRead(output);
        var info = Bundle.Read(exe)!;
        var first = Bundle.Unpack(exe, info, cache);
        File.WriteAllText(Path.Combine(first, "Queueify.exe"), "marker");

        var second = Bundle.Unpack(exe, info, cache);

        Assert.Equal(first, second);
        Assert.Equal("marker", File.ReadAllText(Path.Combine(second, "Queueify.exe")));
    }

    [Fact]
    public void A_half_finished_unpack_is_done_again()
    {
        var output = Path.Combine(_dir, "Queueify.exe");
        Bundle.Write(Starter(), App("app"), output);
        var cache = Path.Combine(_dir, "cache");
        using var exe = File.OpenRead(output);
        var info = Bundle.Read(exe)!;
        // What a crash or a full disk mid-unpack leaves: the folder, but not every file.
        var broken = Path.Combine(cache, info.Id);
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "Queueify.exe"), "half");

        var unpacked = Bundle.Unpack(exe, info, cache);

        Assert.Equal("the app", File.ReadAllText(Path.Combine(unpacked, "Queueify.exe")));
        Assert.True(File.Exists(Path.Combine(unpacked, "resources.pri")));
    }

    [Fact]
    public void Older_versions_are_removed_and_the_current_one_kept()
    {
        var cache = Path.Combine(_dir, "cache");
        foreach (var id in new[] { "old1", "old2", "current" }) Directory.CreateDirectory(Path.Combine(cache, id));

        Bundle.RemoveOtherVersions(cache, "current");

        Assert.Equal(["current"], Directory.GetDirectories(cache).Select(Path.GetFileName));
    }

    [Fact]
    public void A_version_still_running_is_left_for_next_time()
    {
        var cache = Path.Combine(_dir, "cache");
        var running = Path.Combine(cache, "running");
        Directory.CreateDirectory(Path.Combine(running, "Assets"));
        File.WriteAllText(Path.Combine(running, "Assets", "Queueify.ico"), "icon");
        Directory.CreateDirectory(Path.Combine(cache, "current"));
        using var inUse = File.Open(Path.Combine(running, "Queueify.exe"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        Bundle.RemoveOtherVersions(cache, "current");

        // Untouched, not half deleted: the running copy still reads its other files.
        Assert.True(File.Exists(Path.Combine(running, "Assets", "Queueify.ico")));
        Assert.True(Directory.Exists(Path.Combine(cache, "current")));
    }
}
