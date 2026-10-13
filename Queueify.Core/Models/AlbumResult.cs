namespace Queueify.Models;

/// <summary>
/// One album in the search results, merged across services: the same record found on Spotify
/// and on Tidal shows up once, and adding it stores both links.
/// </summary>
public sealed class AlbumResult
{
    public AlbumResult(AlbumMatch first, string titleKey, string artistKey)
    {
        TitleKey = titleKey;
        ArtistKey = artistKey;
        Absorb(first);
    }

    public string TitleKey { get; }
    public string ArtistKey { get; private set; }
    public AlbumMatch? Spotify { get; private set; }
    public AlbumMatch? Tidal { get; private set; }
    public double Score { get; private set; }

    /// <summary>Set when this album is already saved: "In your queue" or "Listened".</summary>
    public string? QueueState { get; set; }

    /// <summary>Adds the other service's version; a second edition from the same service is ignored.</summary>
    public void Absorb(AlbumMatch m)
    {
        if (m.Service == MusicService.Spotify) Spotify ??= m;
        else Tidal ??= m;
        Score = Math.Max(Score, m.Score);
        if (ArtistKey.Length == 0 && m.Artists.Count > 0) ArtistKey = Services.Matcher.Normalize(m.Artists[0]);
    }

    private IEnumerable<AlbumMatch> Versions => new[] { Spotify, Tidal }.OfType<AlbumMatch>();

    /// <summary>The album's plain name, without "(Remastered 2011)"-style edition suffixes.</summary>
    public string Title => Versions.Select(v => Services.Matcher.CleanTitle(v.Title)).OrderBy(t => t.Length).First();

    public string Artist => Versions.Select(v => v.Artist).FirstOrDefault(a => a.Length > 0) ?? "";

    /// <summary>The earliest year across services: closest to the original release.</summary>
    public int? Year => Versions.Select(v => v.Year).Where(y => y != null).Min();

    public string? CoverUrl => Spotify?.CoverUrl ?? Tidal?.CoverUrl;

    public string Details
    {
        get
        {
            var parts = new List<string>();
            if (Artist.Length > 0) parts.Add(Artist);
            if (Year is int y) parts.Add(y.ToString());
            var type = Versions.Select(v => v.AlbumType).FirstOrDefault(t => !string.IsNullOrEmpty(t));
            if (type != null && !type.Equals("album", StringComparison.OrdinalIgnoreCase)) parts.Add(type.ToLowerInvariant());
            return string.Join("  ·  ", parts);
        }
    }

    public double SpotifyOpacity => Spotify != null ? 1 : 0.3;
    public double TidalOpacity => Tidal != null ? 1 : 0.3;

    public bool CanAdd => QueueState == null;
    public bool HasQueueState => QueueState != null;

    public string ScoreText => Score switch
    {
        >= 0.8 => "Best match",
        >= 0.55 => "Possible match",
        _ => "",
    };
}
