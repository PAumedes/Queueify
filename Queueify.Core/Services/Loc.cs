using System.Globalization;

namespace Queueify.Services;

/// <summary>
/// The app's texts in English and Spanish (Strings.En.cs, Strings.Es.cs). The language is chosen
/// once at startup (Settings → Language, or Windows' language); changing it restarts the app.
/// </summary>
public static partial class Loc
{
    private static string _language = "en";
    private static readonly AsyncLocal<string?> Scoped = new();

    /// <summary>"en" or "es".</summary>
    public static string Language => Scoped.Value ?? _language;

    public static IReadOnlyDictionary<string, string> English => En;
    public static IReadOnlyDictionary<string, string> Spanish => Es;

    /// <summary>
    /// "system" (or anything unknown) follows Windows: Spanish if Windows is in Spanish, English
    /// otherwise; "en" and "es" are explicit choices.
    /// </summary>
    public static string Resolve(string preference, CultureInfo windows) => preference switch
    {
        "en" or "es" => preference,
        _ => windows.TwoLetterISOLanguageName == "es" ? "es" : "en",
    };

    /// <summary>
    /// Formats for dates and numbers: Windows' own regional formats when they're in the app's
    /// language, otherwise Argentina for Spanish and the US for English.
    /// </summary>
    public static CultureInfo CultureFor(string language, CultureInfo windows) =>
        windows.TwoLetterISOLanguageName == language
            ? windows
            : CultureInfo.GetCultureInfo(language == "es" ? "es-AR" : "en-US");

    /// <summary>Windows' own display language, read before <see cref="Use"/> replaces the thread's culture.</summary>
    public static CultureInfo WindowsLanguage { get; } = CultureInfo.CurrentUICulture;

    /// <summary>Sets the app language for this run, and the formats that go with it.</summary>
    public static void Use(string preference)
    {
        var windows = WindowsLanguage;
        _language = Resolve(preference, windows);
        var culture = CultureFor(_language, windows);
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = culture;
    }

    /// <summary>The text for <paramref name="key"/>; English if a translation is missing, the key if both are.</summary>
    public static string T(string key) =>
        (Language == "es" && Es.TryGetValue(key, out var es)) ? es
        : En.TryGetValue(key, out var en) ? en
        : key;

    /// <summary>The text for <paramref name="key"/> with its {0}, {1}… filled in.</summary>
    public static string F(string key, params object?[] args) => string.Format(Culture, T(key), args);

    /// <summary>Formats for numbers and dates in the app language (see <see cref="CultureFor"/>).</summary>
    public static CultureInfo Culture => CultureFor(Language, CultureInfo.CurrentCulture);

    /// <summary>For tests: a language for the current async flow only, undone on dispose.</summary>
    public static IDisposable Override(string language)
    {
        var before = Scoped.Value;
        Scoped.Value = language;
        return new Restore(() => Scoped.Value = before);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
