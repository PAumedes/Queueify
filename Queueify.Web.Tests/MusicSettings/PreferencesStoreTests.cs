using Queueify.Web.Services;
using Queueify.Web.Storage;

namespace Queueify.Web.Tests.MusicSettings;

public sealed class PreferencesStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qp-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    [Fact]
    public void Defaults_follow_the_device_and_read_spanish_and_english()
    {
        var p = new PreferencesStore(new UserStores(_root)).Load("alice");
        Assert.Equal(("system", "system", "auto", false), (p.Theme, p.Language, p.OcrLanguage, p.TourSeen));
    }

    [Fact]
    public void Saved_preferences_come_back_and_belong_to_one_user()
    {
        var store = new PreferencesStore(new UserStores(_root));
        store.Save("alice", new Preferences("dark", "es", "en", true));

        Assert.Equal(new Preferences("dark", "es", "en", true), store.Load("alice"));
        Assert.Equal("system", store.Load("bob").Theme);
    }

    [Fact]
    public void Values_outside_the_choices_are_replaced_when_read_and_refused_when_saved()
    {
        var stores = new UserStores(_root);
        Directory.CreateDirectory(Path.Combine(_root, "alice"));
        File.WriteAllText(Path.Combine(_root, "alice", "prefs.json"), """{"Theme":"<script>","Language":"xx","OcrLanguage":"spa+eng;","TourSeen":true}""");
        var store = new PreferencesStore(stores);

        Assert.Equal(new Preferences("system", "system", "auto", true), store.Load("alice"));
        Assert.Throws<ArgumentException>(() => store.Save("alice", new Preferences("neon", "es", "auto", false)));
    }

    // ── the file is parsed once, not on every request ──

    [Fact]
    public void An_unchanged_file_is_not_read_and_parsed_again()
    {
        var stores = new UserStores(_root);
        var store = new PreferencesStore(stores);
        store.Save("alice", new Preferences("dark", "es", "auto", false));
        var path = stores.FileOf("alice", "prefs.json");
        Assert.Equal("dark", store.Load("alice").Theme);

        // Same length, same time stamp, other content: only a re-read would notice.
        var stamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, File.ReadAllText(path).Replace("dark", "dusk").Replace("\"dusk\"", "\"lite\""));
        File.SetLastWriteTimeUtc(path, stamp);

        Assert.Equal("dark", store.Load("alice").Theme);
    }

    [Fact]
    public void A_file_changed_by_someone_else_is_read_again()
    {
        var stores = new UserStores(_root);
        var store = new PreferencesStore(stores);
        store.Save("alice", new Preferences("dark", "es", "auto", false));
        Assert.Equal("dark", store.Load("alice").Theme);

        var path = stores.FileOf("alice", "prefs.json");
        File.WriteAllText(path, """{"Theme":"light","Language":"es","OcrLanguage":"auto","TourSeen":true}""");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));

        Assert.Equal(new Preferences("light", "es", "auto", true), store.Load("alice"));
    }

    [Fact]
    public void Saving_shows_at_once_and_deleting_the_folder_gives_the_defaults_again()
    {
        var stores = new UserStores(_root);
        var store = new PreferencesStore(stores);
        store.Save("alice", new Preferences("dark", "es", "auto", false));
        store.Save("alice", new Preferences("light", "en", "auto", true));
        Assert.Equal(new Preferences("light", "en", "auto", true), store.Load("alice"));

        stores.Delete("alice");

        Assert.Equal(Preferences.Default, store.Load("alice"));
    }

    [Fact]
    public void A_damaged_file_gives_the_defaults()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alice"));
        File.WriteAllText(Path.Combine(_root, "alice", "prefs.json"), "{{{");
        Assert.Equal("system", new PreferencesStore(new UserStores(_root)).Load("alice").Theme);
    }
}
