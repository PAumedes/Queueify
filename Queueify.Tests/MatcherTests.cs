using Queueify.Models;
using Queueify.Services;

namespace Queueify.Tests;

public class MatcherTests
{
    private static AlbumMatch Spotify(string title, string artist, int? year, string type = "album") =>
        new() { Service = MusicService.Spotify, Id = "s:" + title, Title = title, Artists = [artist], Year = year, AlbumType = type };

    private static AlbumMatch Tidal(string title, string artist, int? year) =>
        new() { Service = MusicService.Tidal, Id = "t:" + title, Title = title, Artists = [artist], Year = year, AlbumType = "ALBUM" };

    private static List<AlbumResult> Search(string raw, AlbumMatch[] spotify, AlbumMatch[] tidal) =>
        Matcher.Combine(spotify, tidal, raw);

    [Theory]
    [InlineData("Prince 1999", "1999", "Prince", 1982)]
    [InlineData("Taylor Swift 1989", "1989", "Taylor Swift", 2014)]
    [InlineData("Dr. Dre 2001", "2001", "Dr. Dre", 1999)]
    public void Combine_FindsAlbumsNamedAfterAYear(string raw, string title, string artist, int released)
    {
        var results = Search(raw, [Spotify(title, artist, released), Spotify("Greatest Hits", artist, released - 3)], []);

        Assert.NotEmpty(results);
        Assert.Equal(title, results[0].Title);
        Assert.True(results[0].Score >= 0.8, $"score {results[0].Score}");
    }

    [Fact]
    public void ShouldRetryWithYear_WhenTheYearMightBeTheTitle()
    {
        // "Prince 1999" first searches "Prince"; if that finds nothing convincing, search again with the number.
        var weak = Search("Prince 1999", [Spotify("Purple Rain", "Prince", 1984)], []);
        Assert.True(Matcher.ShouldRetryWithYear("Prince 1999", weak));

        var strong = Search("Prince 1999", [Spotify("1999", "Prince", 1982)], []);
        Assert.False(Matcher.ShouldRetryWithYear("Prince 1999", strong));

        Assert.False(Matcher.ShouldRetryWithYear("abbey road", []), "no year, nothing to retry");
        Assert.False(Matcher.ShouldRetryWithYear("1999", []), "the year was never removed");
    }

    [Theory]
    [InlineData("charly garcia clics modernos 1983", "charly garcia clics modernos")]
    [InlineData("1999", "1999")]
    [InlineData("  ", "")]
    public void SearchText_DropsTheYearUnlessNothingElseIsLeft(string raw, string expected)
    {
        Assert.Equal(expected, Matcher.SearchText(raw));
    }

    [Theory]
    [InlineData("charly garcia clics modernos 1983", "charly garcia clics modernos", 1983)]
    [InlineData("Bajo Belgrano Spinetta Jade (1983)", "Bajo Belgrano Spinetta Jade", 1983)]
    [InlineData("abbey road", "abbey road", null)]
    public void ExtractYear_SplitsTheYearFromTheQuery(string raw, string text, int? year)
    {
        Assert.Equal((text, year), Matcher.ExtractYear(raw));
    }

    [Fact]
    public void Combine_MergesTheSameAlbumFromBothServices_AndPrefersTheOriginal()
    {
        var results = Search("charly garcia clics modernos 1984",
            [Spotify("Clics Modernos (Remasterizado 2023)", "Charly García", 2023), Spotify("Piano Bar", "Charly García", 1984)],
            [Tidal("Clics Modernos", "Charly García", 1983)]);

        var best = results[0];
        Assert.Equal("Clics Modernos", best.Title);
        Assert.Equal(1983, best.Year);
        Assert.NotNull(best.Spotify);
        Assert.NotNull(best.Tidal);
        Assert.True(best.Score >= 0.8, $"score {best.Score}");
    }

    [Fact]
    public void Combine_ToleratesOcrTyposAllCapsAndMissingAccents()
    {
        var results = Search("Los Fabulosos Cadlllacs VASOS VACIOS",
            [Spotify("Vasos Vacíos", "Los Fabulosos Cadillacs", 1993), Spotify("Fabulosos Calavera", "Los Fabulosos Cadillacs", 1997)],
            []);

        Assert.Equal("Vasos Vacíos", results[0].Title);
        Assert.True(results[0].Score >= 0.8, $"score {results[0].Score}");
    }

    [Fact]
    public void Combine_DoesNotCareAboutWordOrder()
    {
        var results = Search("Bajo Belgrano Spinetta Jade 1983",
            [Spotify("Bajo Belgrano", "Spinetta Jade", 1983), Spotify("Alma de Diamante", "Spinetta Jade", 1980)],
            [Tidal("Bajo Belgrano (En Vivo)", "Luis Alberto Spinetta", 2010)]);

        Assert.Equal("Spinetta Jade", results[0].Artist);
        Assert.Equal("Bajo Belgrano", results[0].Title);
        Assert.Equal(1983, results[0].Year);
    }

    [Fact]
    public void Combine_GivesNoConfidentMatchForUnrelatedResults()
    {
        var results = Search("artista inexistente disco raro", [Spotify("Greatest Hits", "Someone", 2001)], []);
        Assert.True(results[0].Score < 0.55, $"score {results[0].Score}");
    }

    [Theory]
    [InlineData("Abbey Road (Remastered 2009)", "Abbey Road")]
    [InlineData("Abbey Road (Super Deluxe Edition)", "Abbey Road")]
    [InlineData("Clics Modernos (Remasterizado 2023)", "Clics Modernos")]
    [InlineData("Let It Be - Remastered", "Let It Be")]
    [InlineData("Mono", "Mono")]
    public void CleanTitle_DropsEditionSuffixes(string title, string expected)
    {
        Assert.Equal(expected, Matcher.CleanTitle(title));
    }

    [Theory]
    [InlineData("Charly García", "charly garcia")]
    [InlineData("The Beatles", "beatles")]
    [InlineData("Simon & Garfunkel", "simon and garfunkel")]
    public void Normalize_IgnoresAccentsCaseAndArticles(string input, string expected)
    {
        Assert.Equal(expected, Matcher.Normalize(input));
    }
}
