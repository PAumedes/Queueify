using Queueify.Models;
using Queueify.Services;

namespace Queueify.Tests;

/// <summary>What the queue pages list: filtering, ordering, and the add box's text handling.</summary>
public class QueueViewTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Local);

    private static BacklogItem Item(string artist, string album, int? year = null, string? source = null, int addedDays = 0, int? listenedDays = null) => new()
    {
        Artist = artist,
        Album = album,
        Year = year,
        Source = source,
        AddedAt = T0.AddDays(addedDays),
        ListenedAt = listenedDays is int d ? T0.AddDays(d) : null,
    };

    private static readonly BacklogItem[] Sample =
    [
        Item("Soda Stereo", "Signos", 1986, "Rolling Stone", addedDays: 1),
        Item("Charly García", "Clics Modernos", 1983, "Pelo", addedDays: 3),
        Item("Spinetta", "Artaud", 1973, null, addedDays: 2),
        Item("Gustavo Cerati", "Bocanada", 1999, "Rolling Stone", addedDays: 0, listenedDays: 10),
        Item("Fito Páez", "Giros", 1985, "Pelo", addedDays: 4, listenedDays: 20),
    ];

    private static string[] Albums(IEnumerable<BacklogItem> items) => items.Select(i => i.Album).ToArray();

    [Fact]
    public void Pending_AndListened_AreSeparatePages()
    {
        Assert.Equal(["Signos", "Artaud", "Clics Modernos"], Albums(QueueView.Apply(Sample, listened: false, "", "oldest")));
        Assert.Equal(["Giros", "Bocanada"], Albums(QueueView.Apply(Sample, listened: true, "", "oldest")));
    }

    [Fact]
    public void Listened_DefaultsToMostRecentlyHeardFirst() =>
        Assert.Equal(["Giros", "Bocanada"], Albums(QueueView.Apply(Sample, true, "", "oldest")));

    [Theory]
    [InlineData("newest", new[] { "Clics Modernos", "Artaud", "Signos" })]
    [InlineData("artist", new[] { "Clics Modernos", "Signos", "Artaud" })]
    [InlineData("year", new[] { "Artaud", "Clics Modernos", "Signos" })]
    [InlineData("source", new[] { "Clics Modernos", "Signos", "Artaud" })]   // no magazine goes last
    public void Sort_OrdersTheQueue(string sort, string[] expected) =>
        Assert.Equal(expected, Albums(QueueView.Apply(Sample, false, "", sort)));

    [Fact]
    public void UnknownSort_FallsBackToOldest() =>
        Assert.Equal(["Signos", "Artaud", "Clics Modernos"], Albums(QueueView.Apply(Sample, false, "", "bogus")));

    [Theory]
    [InlineData("clics", new[] { "Clics Modernos" })]
    [InlineData("GARCIA", new[] { "Clics Modernos" })]          // accents and case don't matter
    [InlineData("rolling", new[] { "Signos" })]                  // the magazine counts
    [InlineData("1973", new[] { "Artaud" })]                     // so does the year
    [InlineData("zzz", new string[0])]
    public void Filter_MatchesArtistAlbumMagazineAndYear(string filter, string[] expected) =>
        Assert.Equal(expected, Albums(QueueView.Apply(Sample, false, filter, "oldest")));

    [Fact]
    public void Apply_DoesNotShowDeletedAlbums()
    {
        var gone = Item("A", "Gone", addedDays: 1);
        gone.Deleted = true;
        Assert.Empty(QueueView.Apply([gone], false, "", "oldest"));
    }

    [Theory]
    [InlineData("Soda Stereo - Signos 1986", "Soda Stereo", "Signos", 1986)]
    [InlineData("Soda Stereo – Signos", "Soda Stereo", "Signos", null)]
    [InlineData("Signos", "", "Signos", null)]
    [InlineData("Charly García - Clics Modernos (1983)", "Charly García", "Clics Modernos", 1983)]
    public void ParseTyped_SplitsArtistAndAlbum(string typed, string artist, string album, int? year)
    {
        var parsed = QueueView.ParseTyped(typed)!;
        Assert.Equal((artist, album, year), (parsed.Artist, parsed.Album, parsed.Year));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseTyped_NothingTyped_IsNull(string typed) => Assert.Null(QueueView.ParseTyped(typed));

    [Fact]
    public void FindSaved_RecognisesAnAlbumByLinkOrByName()
    {
        var saved = new BacklogItem { Artist = "Soda Stereo", Album = "Signos", SpotifyId = "sp1" };
        var byLink = Result(spotifyId: "sp1", title: "Other", artist: "Other");
        var byName = Result(title: "Signos (Remastered 2007)", artist: "soda stereo");
        var other = Result(title: "Dynamo", artist: "Soda Stereo");

        Assert.Same(saved, QueueView.FindSaved(byLink, [saved]));
        Assert.Same(saved, QueueView.FindSaved(byName, [saved]));
        Assert.Null(QueueView.FindSaved(other, [saved]));
    }

    private static AlbumResult Result(string title, string artist, string? spotifyId = null)
    {
        var m = new AlbumMatch { Service = MusicService.Spotify, Id = spotifyId ?? "zz", Title = title, Artists = [artist] };
        return new AlbumResult(m, Matcher.Normalize(title), Matcher.Normalize(artist));
    }
}
