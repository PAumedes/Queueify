using Queueify.Models;

namespace Queueify.Services;

/// <summary>An album typed by hand.</summary>
public sealed record TypedAlbum(string Artist, string Album, int? Year);

/// <summary>What the queue pages list and how the add box reads what was typed: pure, so every front end agrees.</summary>
public static class QueueView
{
    /// <summary>The orders offered, in menu order; "oldest" is also the fallback for anything unknown.</summary>
    public static readonly IReadOnlyList<string> SortKeys = ["oldest", "newest", "artist", "year", "source"];

    /// <summary>The pending (or listened) albums that match <paramref name="filter"/>, in the chosen order.</summary>
    public static List<BacklogItem> Apply(IEnumerable<BacklogItem> all, bool listened, string filter, string sort)
    {
        var words = Matcher.Normalize(filter);
        var items = all
            .Where(i => !i.Deleted && i.IsListened == listened)
            .Where(i => words.Length == 0 || Matcher.Normalize($"{i.Artist} {i.Album} {i.Source} {i.Year}").Contains(words));

        var text = StringComparer.Create(Loc.Culture, ignoreCase: true);
        items = sort switch
        {
            "newest" => items.OrderByDescending(i => i.AddedAt),
            "artist" => items.OrderBy(i => i.Artist, text).ThenBy(i => i.Year),
            "year" => items.OrderBy(i => i.Year ?? int.MaxValue).ThenBy(i => i.Artist, text),
            "source" => items.OrderBy(i => i.Source ?? "￿", text).ThenBy(i => i.AddedAt),
            _ => listened ? items.OrderByDescending(i => i.ListenedAt) : items.OrderBy(i => i.AddedAt),
        };
        return items.ToList();
    }

    /// <summary>
    /// What "Add without a link" saves from the typed line: "Artist - Album 1999" is split, anything
    /// else is kept whole as the album. Null when nothing was typed.
    /// </summary>
    public static TypedAlbum? ParseTyped(string typed)
    {
        var (text, year) = Matcher.ExtractYear(typed.Trim());
        if (text.Length == 0) return null;
        var parts = text.Split([" - ", " – ", " — "], 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2 ? new(parts[0], parts[1], year) : new("", text, year);
    }

    /// <summary>The saved album a search result is already, by service link or by name; null when it is new.</summary>
    public static BacklogItem? FindSaved(AlbumResult result, IEnumerable<BacklogItem> saved)
    {
        var title = Matcher.Normalize(result.Title);
        var artist = Matcher.Normalize(result.Artist);
        return saved.FirstOrDefault(i =>
            (result.Spotify != null && i.SpotifyId == result.Spotify.Id) ||
            (result.Tidal != null && i.TidalId == result.Tidal.Id) ||
            (Matcher.Normalize(i.Album) == title && Matcher.Normalize(i.Artist) == artist));
    }
}
