using System.Collections.Concurrent;
using System.Text.Json;
using Queueify.Web.Storage;

namespace Queueify.Web.Services;

/// <summary>What a user picked for how the app looks and reads photos; per user, never synced with PCs.</summary>
/// <param name="Theme">"system", "light" or "dark".</param>
/// <param name="Language">"system", "en" or "es".</param>
/// <param name="OcrLanguage">"auto", "es" or "en".</param>
/// <param name="TourSeen">The quick tour has been shown once.</param>
public sealed record Preferences(string Theme, string Language, string OcrLanguage, bool TourSeen)
{
    public static Preferences Default { get; } = new("system", "system", "auto", false);

    public static readonly string[] Themes = ["system", "light", "dark"];
    public static readonly string[] Languages = ["system", "en", "es"];
    public static readonly string[] OcrLanguages = ["auto", "es", "en"];

    public bool IsValid => Themes.Contains(Theme) && Languages.Contains(Language) && OcrLanguages.Contains(OcrLanguage);
}

/// <summary>prefs.json in the user's folder. Anything unreadable or out of range reads as the default.</summary>
public sealed class PreferencesStore(UserStores stores)
{
    // Every signed-in request asks for the language and theme, so the parsed file is kept and trusted for as long as
    // its size and time stamp are the ones it was read at. That still sees a save, a deletion or an edit from outside.
    private readonly ConcurrentDictionary<string, (long Ticks, long Length, Preferences Value)> _cache = new();

    public Preferences Load(string userId)
    {
        try
        {
            var path = stores.FileOf(userId, "prefs.json");
            var info = new FileInfo(path);
            if (!info.Exists) { _cache.TryRemove(path, out _); return Preferences.Default; }
            if (_cache.TryGetValue(path, out var cached) && cached.Ticks == info.LastWriteTimeUtc.Ticks && cached.Length == info.Length) return cached.Value;

            var read = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path)) ?? Preferences.Default;
            var value = new Preferences(
                Pick(read.Theme, Preferences.Themes, "system"),
                Pick(read.Language, Preferences.Languages, "system"),
                Pick(read.OcrLanguage, Preferences.OcrLanguages, "auto"),
                read.TourSeen);
            _cache[path] = (info.LastWriteTimeUtc.Ticks, info.Length, value);
            return value;
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            return Preferences.Default;
        }
    }

    public void Save(string userId, Preferences preferences)
    {
        if (!preferences.IsValid) throw new ArgumentException("Not an offered choice.", nameof(preferences));
        var path = stores.FileOf(userId, "prefs.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Written beside and moved into place, so a read during a write (another tab) never sees half a file.
        var partial = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(partial, JsonSerializer.Serialize(preferences));
        File.Move(partial, path, overwrite: true);
        _cache.TryRemove(path, out _);   // the next read parses what was just written
    }

    private static string Pick(string? value, string[] choices, string fallback) => value is not null && choices.Contains(value) ? value : fallback;
}
