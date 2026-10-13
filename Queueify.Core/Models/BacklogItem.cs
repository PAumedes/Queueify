using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Queueify.Models;

/// <summary>An album saved from a magazine, waiting to be listened to (or already listened).</summary>
public sealed class BacklogItem : INotifyPropertyChanged
{
    private double _rating = -1;

    /// <summary>Local database row id (differs between PCs).</summary>
    public long Id { get; set; }

    /// <summary>Stable id shared by every PC; the album's document id online.</summary>
    public string Uid { get; set; } = "";

    /// <summary>Last change (UTC). When two PCs changed the same album, the newer change wins.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>Changed on this PC and not yet uploaded.</summary>
    public bool Dirty { get; set; }

    /// <summary>Deleted, but kept until the deletion has been uploaded so other PCs remove it too.</summary>
    public bool Deleted { get; set; }

    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public int? Year { get; set; }
    public string? Source { get; set; }
    public string? CoverUrl { get; set; }
    public string? SpotifyId { get; set; }
    public string? TidalId { get; set; }
    public DateTime AddedAt { get; set; }
    public DateTime? ListenedAt { get; set; }

    /// <summary>1-5 stars, or -1 when unrated (RatingControl's "no value").</summary>
    public double Rating
    {
        get => _rating;
        set { if (_rating != value) { _rating = value; OnPropertyChanged(); } }
    }

    /// <summary>Which app the Play button opens when an album is on both services.</summary>
    public static MusicService Preferred { get; set; } = MusicService.Tidal;

    public bool HasSpotify => !string.IsNullOrEmpty(SpotifyId);
    public bool HasTidal => !string.IsNullOrEmpty(TidalId);
    public bool IsListened => ListenedAt != null;
    public bool IsPending => ListenedAt == null;

    public MusicService? PlayService => Preferred == MusicService.Tidal
        ? (HasTidal ? MusicService.Tidal : HasSpotify ? MusicService.Spotify : null)
        : (HasSpotify ? MusicService.Spotify : HasTidal ? MusicService.Tidal : null);

    public bool CanPlay => PlayService != null;
    public string PlayLabel => PlayService?.ToString() ?? Services.Loc.T("item.no_link");
    public string ArtistLine => Artist.Length > 0 ? Artist : Services.Loc.T("item.not_found");

    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (Year is int y) parts.Add(y.ToString());
            if (!string.IsNullOrWhiteSpace(Source)) parts.Add(Source!);
            parts.Add(ListenedAt is DateTime l ? Services.Loc.F("item.listened_on", l) : Services.Loc.F("item.added_on", AddedAt));
            return string.Join("  ·  ", parts);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
