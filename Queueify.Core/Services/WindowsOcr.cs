namespace Queueify.Services;

/// <summary><see cref="IOcr"/> on the engine built into Windows; the desktop app's reader.</summary>
public sealed class WindowsOcr : IOcr
{
    public async Task<OcrReading> RecognizeAsync(Stream image, string language, CancellationToken ct = default)
    {
        var page = await OcrService.RecognizeAsync(image.AsRandomAccessStream(), PreferenceFor(language, OcrService.InstalledTags));
        using var bitmap = page.Bitmap;
        var lines = page.Lines
            .Select(l => new OcrTextLine(l.Text, new OcrBox(l.Bounds.X, l.Bounds.Y, l.Bounds.Width, l.Bounds.Height)))
            .ToList();
        return new OcrReading(lines, bitmap.PixelWidth, bitmap.PixelHeight, page.LanguageName);
    }

    /// <summary>
    /// A primary tag ("en") becomes the first installed Windows language that starts with it; "auto" and
    /// anything not installed stay as they are, so <see cref="OcrLanguages.Choose"/> falls back to Spanish.
    /// </summary>
    public static string PreferenceFor(string language, IEnumerable<string> installed) =>
        language.Length is 2 && installed.FirstOrDefault(t => t.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase)) is { } tag
            ? tag
            : language;
}
