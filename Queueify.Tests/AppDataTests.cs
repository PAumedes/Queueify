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

    private const string Local = @"C:\Users\ana\AppData\Local";

    // What's in the folder called Data next to the exe; null when there's no such folder.
    private static Func<string, string[]?> DataFolderHolds(string expectedPath, params string[] entries) =>
        path => path == expectedPath ? entries : null;

    [Fact]
    public void Data_lives_in_the_users_local_app_data()
    {
        Assert.Equal(@"C:\Users\ana\AppData\Local\Queueify",
            AppData.FolderFor(@"C:\Users\ana\Documents\Queueify\Queueify.exe", Local, _ => null));
    }

    [Theory]
    [InlineData("queueify.db")]   // how 1.1.0 and earlier kept everything
    [InlineData("settings.json")]
    [InlineData("ui.json")]
    [InlineData("QUEUEIFY.DB")]
    public void A_Data_folder_with_Queueify_files_next_to_the_exe_keeps_the_app_portable(string file)
    {
        Assert.Equal(@"E:\Queueify\Data",
            AppData.FolderFor(@"E:\Queueify\Queueify.exe", Local, DataFolderHolds(@"E:\Queueify\Data", file, "crash.log")));
    }

    [Fact]
    public void An_empty_Data_folder_next_to_the_exe_starts_a_portable_copy()
    {
        Assert.Equal(@"E:\Queueify\Data", AppData.FolderFor(@"E:\Queueify\Queueify.exe", Local, DataFolderHolds(@"E:\Queueify\Data")));
    }

    [Fact]
    public void Someone_elses_Data_folder_is_left_alone()
    {
        // Documents\Data or Downloads\Data with the user's own files isn't Queueify's.
        Assert.Equal(@"C:\Users\ana\AppData\Local\Queueify",
            AppData.FolderFor(@"C:\Users\ana\Documents\Queueify.exe", Local, DataFolderHolds(@"C:\Users\ana\Documents\Data", "taxes.txt")));
    }

    [Fact]
    public void Without_a_known_exe_path_the_data_is_in_local_app_data()
    {
        Assert.Equal(@"C:\Users\ana\AppData\Local\Queueify", AppData.FolderFor(null, Local, _ => []));
    }

    [Theory]
    // The download unpacks the app and runs it from its cache; the starter says which exe the user opened.
    [InlineData(@"E:\Queueify\Queueify.exe", @"C:\Users\ana\AppData\Local\Queueify\app\1a2b\Queueify.exe", @"E:\Queueify\Queueify.exe")]
    [InlineData(null, @"E:\Queueify\Queueify.exe", @"E:\Queueify\Queueify.exe")]
    [InlineData("", @"E:\Queueify\Queueify.exe", @"E:\Queueify\Queueify.exe")]
    [InlineData("Queueify.exe", @"E:\Queueify\Queueify.exe", @"E:\Queueify\Queueify.exe")]
    public void The_exe_the_user_started_is_the_starter_when_there_is_one(string? starter, string processPath, string expected)
    {
        Assert.Equal(expected, AppData.StartedExe(starter, processPath));
    }

    [Theory]
    // A taskbar pin made while the app runs from its unpacked copy must start the download's exe instead:
    // the copy goes away with the next version.
    [InlineData(@"E:\Queueify\Queueify.exe", "\"E:\\Queueify\\Queueify.exe\"")]
    [InlineData(@"C:\Users\Ana María\Apps\Queueify (1).exe", "\"C:\\Users\\Ana María\\Apps\\Queueify (1).exe\"")]
    public void A_pinned_Queueify_starts_the_exe_the_user_opened(string started, string expected)
    {
        Assert.Equal(expected, AppData.RelaunchCommand(started));
    }

    [Theory]
    [InlineData(@"D:\Scratch\local", @"D:\Scratch\local")]
    [InlineData(null, Local)]
    [InlineData("", Local)]
    [InlineData("rel", Local)]                 // relative: would depend on the folder the app starts in
    [InlineData(@"""C:\Users\ana""", Local)]   // quoted
    [InlineData(@"\Users\ana", Local)]         // rooted but no drive
    [InlineData(@"C:\Users\ana""", Local)]     // trailing quote
    [InlineData(@"C:\x<y", Local)]
    [InlineData(@"C:\a*b", Local)]
    [InlineData(@"C:\a?b", Local)]
    [InlineData(@"C:\a|b", Local)]
    public void Local_app_data_comes_from_the_environment_only_when_it_is_a_full_path(string? variable, string expected)
    {
        Assert.Equal(expected, AppData.LocalAppDataRoot(variable, Local));
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
              "PreferredService": "Spotify",
              "CountryCode": "ES",
              "LastSource": "Rolling Stone #312",
              "SharedSettingsUpdatedAt": "2026-10-11T22:04:51.1234567Z",
              "CloudProjectId": "my-project",
              "CloudQueueKey": "",
              "CloudLastServerTime": null,
              "SpotifyConfigured": true,
              "TidalConfigured": false,
              "CloudConfigured": false
            }
            """);

        var loaded = AppData.LoadSettings(path);

        Assert.Equal(
            ("sid", "ssecret", "Spotify", "ES", "Rolling Stone #312", "my-project", (DateTime?)new DateTime(2026, 10, 11, 22, 4, 51, DateTimeKind.Utc).AddTicks(1234567)),
            (loaded.SpotifyClientId, loaded.SpotifyClientSecret, loaded.PreferredService, loaded.CountryCode, loaded.LastSource, loaded.CloudProjectId, loaded.SharedSettingsUpdatedAt?.ToUniversalTime()));
        Assert.Null(loaded.CloudLastServerTime);
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
