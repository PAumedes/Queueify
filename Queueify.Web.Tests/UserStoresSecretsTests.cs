using Microsoft.AspNetCore.DataProtection;
using Queueify.Models;
using Queueify.Web.Storage;

namespace Queueify.Web.Tests;

public sealed class UserStoresSecretsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qw-" + Guid.NewGuid().ToString("N"));
    private readonly IDataProtector _protector = new EphemeralDataProtectionProvider().CreateProtector("test");
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private string SettingsFile => Path.Combine(_root, "alice", "settings.json");

    [Fact]
    public void Client_secrets_are_encrypted_on_disk_and_come_back_readable()
    {
        var stores = new UserStores(_root, _protector);
        stores.SaveSettings("alice", new AppSettings { SpotifyClientId = "sid", SpotifyClientSecret = "spot-s3cret", TidalClientSecret = "tidal-s3cret" });

        var onDisk = File.ReadAllText(SettingsFile);
        Assert.DoesNotContain("spot-s3cret", onDisk);
        Assert.DoesNotContain("tidal-s3cret", onDisk);
        Assert.Contains("sid", onDisk);   // an id is not a secret
        var vault = File.ReadAllText(Path.Combine(_root, "alice", "secrets.dat"));
        Assert.DoesNotContain("s3cret", vault);

        var loaded = stores.LoadSettings("alice");
        Assert.Equal("spot-s3cret", loaded.SpotifyClientSecret);
        Assert.Equal("tidal-s3cret", loaded.TidalClientSecret);
    }

    [Fact]
    public void A_plain_settings_file_is_still_read_and_gets_encrypted_on_the_next_save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        File.WriteAllText(SettingsFile, """{"SpotifyClientSecret":"old-plain"}""");
        var stores = new UserStores(_root, _protector);

        var loaded = stores.LoadSettings("alice");
        Assert.Equal("old-plain", loaded.SpotifyClientSecret);

        stores.SaveSettings("alice", loaded);
        Assert.DoesNotContain("old-plain", File.ReadAllText(SettingsFile));
    }

    [Fact]
    public void A_secret_that_cannot_be_decrypted_reads_as_empty_instead_of_failing()
    {
        new UserStores(_root, _protector).SaveSettings("alice", new AppSettings { SpotifyClientSecret = "x" });
        var otherKeys = new EphemeralDataProtectionProvider().CreateProtector("test");

        Assert.Equal("", new UserStores(_root, otherKeys).LoadSettings("alice").SpotifyClientSecret);
    }

    [Fact]
    public void Saving_does_not_change_the_callers_settings_object()
    {
        var settings = new AppSettings { SpotifyClientSecret = "keep" };
        new UserStores(_root, _protector).SaveSettings("alice", settings);
        Assert.Equal("keep", settings.SpotifyClientSecret);
    }

    [Fact]
    public void Deleting_a_user_removes_their_whole_folder_and_nobody_elses()
    {
        var stores = new UserStores(_root, _protector);
        stores.QueueOf("alice").Insert(new BacklogItem { Artist = "A", Album = "One" });
        stores.QueueOf("bob").Insert(new BacklogItem { Artist = "B", Album = "Two" });

        stores.Delete("alice");

        Assert.False(Directory.Exists(Path.Combine(_root, "alice")));
        Assert.Single(stores.QueueOf("bob").LoadAll());
    }

    [Fact]
    public void Deleting_a_user_who_has_no_data_is_fine() => new UserStores(_root, _protector).Delete("nobody");

    [Fact]
    public void FileOf_stays_inside_the_users_folder()
    {
        var stores = new UserStores(_root);
        Assert.Equal(Path.Combine(_root, "alice", "prefs.json"), stores.FileOf("alice", "prefs.json"));
        Assert.Throws<ArgumentException>(() => stores.FileOf("alice", "../x.json"));
    }
}
