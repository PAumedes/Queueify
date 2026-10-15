using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Queueify.Models;

namespace Queueify.Services;

/// <summary>
/// Scores catalog results against a free-text query typed (or clicked) from a magazine, e.g.
/// "charly garcia clics modernos 1983". Word order doesn't matter, and it tolerates accents,
/// capitalization, OCR typos, "(Remastered)"-style suffixes and a year that is off by one.
/// </summary>
public static partial class Matcher
{
    /// <summary>Pulls a release year out of the query; the rest is what gets searched.</summary>
    public static (string Text, int? Year) ExtractYear(string query)
    {
        var m = YearPattern().Match(query);
        if (!m.Success) return (query.Trim(), null);
        var text = Whitespace().Replace(query.Remove(m.Index, m.Length), " ").Trim(' ', '(', ')', ',', '-');
        return (text, int.Parse(m.Value, CultureInfo.InvariantCulture));
    }

    /// <summary>Score from which a result counts as a confident match (highlighted, added with Enter).</summary>
    public const double ConfidentScore = 0.55;

    /// <summary>
    /// The first search leaves the year out. If that found nothing convincing, the number may be the
    /// title ("Prince 1999", "Bob Dylan 1966"), so it's worth searching again with it.
    /// </summary>
    public static bool ShouldRetryWithYear(string raw, IReadOnlyList<AlbumResult> results)
    {
        var (text, year) = ExtractYear(raw);
        bool confident = results.Count > 0 && results[0].Score >= ConfidentScore;
        return year != null && text.Length > 0 && !confident;
    }

    /// <summary>
    /// What to send to Spotify/Tidal: the query without its year (catalog search doesn't match years),
    /// unless the year is all there is.
    /// </summary>
    public static string SearchText(string raw)
    {
        var (text, _) = ExtractYear(raw);
        return text.Length > 0 ? text : raw.Trim();
    }

    /// <summary>
    /// Scores both services' results against what was typed and merges the same album from Spotify
    /// and Tidal into one row, best match first. A number like 1989 is read both ways, as a release
    /// year and as part of the title ("Taylor Swift 1989", "Prince 1999"), and the better reading wins.
    /// </summary>
    public static List<AlbumResult> Combine(IEnumerable<AlbumMatch> spotify, IEnumerable<AlbumMatch> tidal, string raw)
    {
        var (text, year) = ExtractYear(raw);
        var all = spotify.Concat(tidal).ToList();
        foreach (var m in all)
            m.Score = year == null ? ScoreQuery(text, null, m) : Math.Max(ScoreQuery(text, year, m), ScoreQuery(raw, null, m));

        var results = new List<AlbumResult>();
        foreach (var m in all.OrderByDescending(m => m.Score))
        {
            var titleKey = Normalize(m.Title);
            var artistKey = m.Artists.Count > 0 ? Normalize(m.Artists[0]) : "";
            var existing = results.FirstOrDefault(r => r.TitleKey == titleKey &&
                (r.ArtistKey == artistKey || r.ArtistKey.Length == 0 || artistKey.Length == 0));
            if (existing != null) existing.Absorb(m);
            else results.Add(new AlbumResult(m, titleKey, artistKey));
        }
        return results.OrderByDescending(r => r.Score).Take(12).ToList();
    }

    public static double ScoreQuery(string query, int? year, AlbumMatch m)
    {
        var q = Tokens(query);
        if (q.Count == 0) return 0;
        var title = Tokens(m.Title);
        var artist = Tokens(string.Join(" ", m.Artists));

        double titleCoverage = Coverage(title, q);                  // the album's name appears in the query
        double artistCoverage = Coverage(artist, q);                // the artist's name appears in the query
        double precision = Coverage(q, title.Concat(artist).ToList()); // the query has no unexplained words

        // Magazines date albums by when they reviewed them, and reissues on streaming services
        // often carry the remaster year, so the year only nudges the score, never vetoes a match.
        double yearScore = (year, m.Year) switch
        {
            (int a, int b) when Math.Abs(a - b) <= 1 => 1,
            (int a, int b) when Math.Abs(a - b) <= 3 => 0.5,
            (int, int) => 0,
            _ => 0.5,
        };
        double singlePenalty = string.Equals(m.AlbumType, "single", StringComparison.OrdinalIgnoreCase) ? 0.05 : 0;

        return Math.Clamp(0.4 * titleCoverage + 0.25 * artistCoverage + 0.25 * precision + 0.1 * yearScore - singlePenalty, 0, 1);
    }

    /// <summary>"Abbey Road (Remastered 2009)" → "Abbey Road", keeping the original capitalization.</summary>
    public static string CleanTitle(string title)
    {
        var clean = EditionAfterDash().Replace(EditionInBrackets().Replace(title, ""), "");
        clean = Whitespace().Replace(clean, " ").Trim();
        return clean.Length > 0 ? clean : title;
    }

    /// <summary>Lowercase, no accents, no punctuation, no edition suffixes, no leading "the".</summary>
    public static string Normalize(string s)
    {
        s = s.ToLowerInvariant().Replace("&", " and ");
        s = EditionInBrackets().Replace(s, " ");
        s = EditionAfterDash().Replace(s, " ");

        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }

        s = NonAlphanumeric().Replace(sb.ToString(), " ").Trim();
        if (s.StartsWith("the ", StringComparison.Ordinal)) s = s[4..];
        return s;
    }

    private static List<string> Tokens(string s) =>
        Normalize(s).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>Share of <paramref name="needles"/> found in <paramref name="haystack"/>, allowing small typos.</summary>
    private static double Coverage(List<string> needles, List<string> haystack)
    {
        if (needles.Count == 0) return 0.5;
        return needles.Count(n => haystack.Any(h => SameWord(n, h))) / (double)needles.Count;
    }

    private static bool SameWord(string a, string b)
    {
        if (a == b) return true;
        int shortest = Math.Min(a.Length, b.Length);
        if (shortest < 4) return false;
        int allowed = Math.Max(a.Length, b.Length) >= 8 ? 2 : 1;
        return Math.Abs(a.Length - b.Length) <= allowed && Levenshtein(a, b) <= allowed;
    }

    private static int Levenshtein(string s, string t)
    {
        var prev = new int[t.Length + 1];
        var curr = new int[t.Length + 1];
        for (int j = 0; j <= t.Length; j++) prev[j] = j;

        for (int i = 1; i <= s.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= t.Length; j++)
            {
                int cost = s[i - 1] == t[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[t.Length];
    }

    [GeneratedRegex(@"\b(19[4-9]\d|20[0-4]\d)\b")]
    private static partial Regex YearPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[\(\[][^\)\]]*(remaster|deluxe|edition|edición|expanded|anniversary|aniversario|version|versión|mono|stereo|bonus|reissue|remasterizado)[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex EditionInBrackets();

    [GeneratedRegex(@"\s-\s.*(remaster|deluxe|edition|edición|version|versión|anniversary|aniversario).*$", RegexOptions.IgnoreCase)]
    private static partial Regex EditionAfterDash();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonAlphanumeric();
}
