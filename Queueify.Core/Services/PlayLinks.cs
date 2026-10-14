using Queueify.Models;

namespace Queueify.Services;

/// <summary>The two ways to open an album: the installed app's link, and the web player when no app answers.</summary>
public sealed record PlayLink(string App, string Web);

public static class PlayLinks
{
    public static PlayLink For(MusicService service, string id) => service == MusicService.Spotify
        ? new($"spotify:album:{id}", $"https://open.spotify.com/album/{id}")
        : new($"tidal://album/{id}", $"https://tidal.com/album/{id}");

    /// <summary>
    /// The link for a saved album on <paramref name="preferred"/>, or on the other service when only that
    /// one has it. Ids come from catalogs and from sync, so anything that isn't a plain token is refused
    /// rather than put into a link.
    /// </summary>
    public static PlayLink? Of(BacklogItem item, MusicService preferred)
    {
        var order = preferred == MusicService.Spotify
            ? new[] { MusicService.Spotify, MusicService.Tidal }
            : new[] { MusicService.Tidal, MusicService.Spotify };
        foreach (var service in order)
        {
            var id = service == MusicService.Spotify ? item.SpotifyId : item.TidalId;
            if (IsPlainId(id)) return For(service, id!);
        }
        return null;
    }

    private static bool IsPlainId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 64 && id.All(char.IsAsciiLetterOrDigit);
}
