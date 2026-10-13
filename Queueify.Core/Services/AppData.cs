using System.Text.Json;
using Queueify.Models;

namespace Queueify.Services;

/// <summary>
/// Portable storage: everything lives in a "Data" folder next to Queueify.exe, so copying the app
/// folder to another PC copies the queue, the keys and the online sync connection too.
/// </summary>
public static class AppData
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string Folder { get; } = Path.Combine(AppContext.BaseDirectory, "Data");
    public static string DatabasePath => Path.Combine(Folder, "queueify.db");
    private static string SettingsPath => Path.Combine(Folder, "settings.json");

    public static AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath) && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) is { } settings)
            {
                settings.Normalize();
                return settings;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A damaged settings file shouldn't stop the app; the user can re-enter keys.
        }
        return new AppSettings();
    }

    public static void SaveSettings(AppSettings settings)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
