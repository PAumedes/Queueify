using Queueify.Models;
using Queueify.Services;

namespace Queueify.Tests;

// The test project turns off reflection-based JSON, as trimming does in the published app, so these
// fail if a saved file can only be read through reflection.
public sealed class AppDataTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queueify-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Data_is_next_to_the_exe_not_where_a_single_file_app_unpacks_itself()
    {
        Assert.Equal(Path.Combine(@"D:\Apps\Queueify", "Data"), AppData.FolderFor(@"D:\Apps\Queueify\Queueify.exe"));
    }

    [Fact]
    public void Settings_survive_a_save_and_load()
    {
        var path = Path.Combine(_dir, "settings.json");
        var saved = new AppSettings
        {
            SpotifyClientId = "sid", SpotifyClientSecret = "ssecret",
            TidalClientId = "tid", TidalClientSecret = "tsecret",
            PreferredService = "Spotify", CountryCode = "ES", LastSource = "Rolling Stone #312",
            SharedSettingsUpdatedAt = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc),
            CloudProjectId = "my-project", CloudQueueKey = new string('a', 40),
            CloudLastServerTime = new DateTime(2026, 10, 12, 8, 30, 0, DateTimeKind.Utc),
        };

        AppData.SaveSettings(saved, path);
        var loaded = AppData.LoadSettings(path);

        Assert.Equal(
            (saved.SpotifyClientId, saved.SpotifyClientSecret, saved.TidalClientId, saved.TidalClientSecret, saved.PreferredService,
             saved.CountryCode, saved.LastSource, saved.SharedSettingsUpdatedAt, saved.CloudProjectId, saved.CloudQueueKey, saved.CloudLastServerTime),
            (loaded.SpotifyClientId, loaded.SpotifyClientSecret, loaded.TidalClientId, loaded.TidalClientSecret, loaded.PreferredService,
             loaded.CountryCode, loaded.LastSource, loaded.SharedSettingsUpdatedAt, loaded.CloudProjectId, loaded.CloudQueueKey, loaded.CloudLastServerTime));
    }

    [Fact]
    public void Settings_written_by_earlier_versions_still_load()
    {
        var path = Path.Combine(_dir, "settings.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, """
            {
              "SpotifyClientId": "sid",
              "SpotifyClientSecret": "ssecret",
              "TidalClientId": "",
              "TidalClientSecret": "",
              "PreferredService": "Tidal",
              "CountryCode": "AR",
              "LastSource": "",
              "SharedSettingsUpdatedAt": "2026-10-11T22:04:51.1234567Z",
              "CloudProjectId": "",
              "CloudQueueKey": "",
              "CloudLastServerTime": null
            }
            """);

        var loaded = AppData.LoadSettings(path);

        Assert.Equal(("sid", "ssecret", "AR"), (loaded.SpotifyClientId, loaded.SpotifyClientSecret, loaded.CountryCode));
    }

    [Fact]
    public void A_damaged_settings_file_gives_defaults()
    {
        var path = Path.Combine(_dir, "settings.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "{ not json");

        Assert.Equal("AR", AppData.LoadSettings(path).CountryCode);
    }

    [Fact]
    public void Layout_survives_a_save_and_load()
    {
        var path = Path.Combine(_dir, "ui.json");
        var saved = new UiState
        {
            PaneWidth = 512, PaneVisible = false, CoverView = true, Sort = "artist",
            WindowBounds = [10, 20, 1300, 900], Maximized = true, TourSeen = true,
            Theme = "dark", Language = "es", OcrLanguage = "en-US", OcrHintDismissed = true,
        };

        saved.Save(path);
        var loaded = UiState.Load(path);

        Assert.Equal(
            (saved.PaneWidth, saved.PaneVisible, saved.CoverView, saved.Sort, saved.Maximized, saved.TourSeen, saved.Theme, saved.Language, saved.OcrLanguage, saved.OcrHintDismissed),
            (loaded.PaneWidth, loaded.PaneVisible, loaded.CoverView, loaded.Sort, loaded.Maximized, loaded.TourSeen, loaded.Theme, loaded.Language, loaded.OcrLanguage, loaded.OcrHintDismissed));
        Assert.Equal(saved.WindowBounds, loaded.WindowBounds);
    }
}
