using System.Globalization;

namespace Queueify.Services;

public sealed record OcrLanguageOption(string Tag, string Name);

/// <summary>
/// The text recognition languages Queueify offers to install (Windows "Features on Demand"), and
/// which installed one reads a photo.
/// </summary>
public static class OcrLanguages
{
    /// <summary>Languages magazines are likely to be in. Spanish first: it's what Queueify is for.</summary>
    public static IReadOnlyList<string> Offered { get; } =
        ["es-ES", "es-MX", "en-US", "en-GB", "pt-BR", "pt-PT", "fr-FR", "it-IT", "de-DE"];

    /// <summary>
    /// The Windows capability to install. Only offered tags are accepted, because the name ends up
    /// in a script that runs as administrator.
    /// </summary>
    public static string CapabilityName(string tag) =>
        Offered.Contains(tag) ? $"Language.OCR~~~{tag}~0.0.1.0" : throw new ArgumentException($"Not an offered OCR language: {tag}", nameof(tag));

    /// <summary>"Español (España)", "English (United States)": each name in its own language.</summary>
    public static string NameOf(string tag)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(tag);
            var name = culture.NativeName;
            return name.Length == 0 ? tag : culture.TextInfo.ToUpper(name[0]) + name[1..];
        }
        catch (CultureNotFoundException)
        {
            return tag;
        }
    }

    public static IReadOnlyList<OcrLanguageOption> Installable(IEnumerable<string> installed)
    {
        var have = installed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Offered.Where(t => !have.Contains(t)).Select(t => new OcrLanguageOption(t, NameOf(t))).ToList();
    }

    /// <summary>
    /// The installed language that reads photos: the user's choice if it's still installed,
    /// otherwise Spanish, otherwise null (Windows picks from the user's languages).
    /// </summary>
    public static string? Choose(string preference, IReadOnlyCollection<string> installed)
    {
        var chosen = installed.FirstOrDefault(t => t.Equals(preference, StringComparison.OrdinalIgnoreCase));
        return chosen ?? installed.FirstOrDefault(t => t.StartsWith("es", StringComparison.OrdinalIgnoreCase));
    }
}
