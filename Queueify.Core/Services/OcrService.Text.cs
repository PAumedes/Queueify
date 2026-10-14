using System.Globalization;
using System.Text.RegularExpressions;

namespace Queueify.Services;

// The plain-text half of OcrService: no Windows API in it, so the web build compiles it too.
public static partial class OcrService
{
    /// <summary>
    /// Cleans a clicked OCR line: pulls out a release year, drops "(Label, 1983)"-style notes and
    /// stray quotes, and turns ALL-CAPS magazine headings into Title Case.
    /// </summary>
    public static (string Text, int? Year) CleanLine(string raw)
    {
        int? year = null;
        var ym = YearPattern().Match(raw);
        if (ym.Success) year = int.Parse(ym.Value, CultureInfo.InvariantCulture);

        var text = BracketWithYear().Replace(raw, " ");
        text = text.Trim().Trim('"', '“', '”', '«', '»', '\'', '‘', '’', '-', '–', '—', '•', '·', ':', ',', '.', ' ');
        text = Whitespace().Replace(text, " ");

        // "Mostly uppercase" rather than "all": OCR often turns one capital (e.g. Í) into a lowercase letter.
        var letters = text.Where(char.IsLetter).ToList();
        if (letters.Count >= 3 && letters.Count(char.IsUpper) >= letters.Count * 0.8)
            text =CultureInfo.GetCultureInfo("es-AR").TextInfo.ToTitleCase(text.ToLower(CultureInfo.GetCultureInfo("es-AR")));

        return (text, year);
    }

    [GeneratedRegex(@"\b(19[4-9]\d|20[0-4]\d)\b")]
    private static partial Regex YearPattern();

    [GeneratedRegex(@"[\(\[][^\)\]]*\b(19[4-9]\d|20[0-4]\d)\b[^\)\]]*[\)\]]")]
    private static partial Regex BracketWithYear();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
