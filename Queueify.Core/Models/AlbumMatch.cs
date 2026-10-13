namespace Queueify.Models;

public enum MusicService { Spotify, Tidal }

/// <summary>A catalog search result from Spotify or Tidal.</summary>
public sealed class AlbumMatch
{
    public required MusicService Service { get; init; }
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required IReadOnlyList<string> Artists { get; init; }
    public int? Year { get; init; }
    public string? CoverUrl { get; init; }
    public string? AlbumType { get; init; }
    public double Score { get; set; }

    public string Artist => string.Join(", ", Artists);
}
