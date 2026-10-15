using Queueify.Models;
using Queueify.Services;

namespace Queueify.Tests;

/// <summary>Exact scores and the merge rule of <see cref="Matcher"/>, so a changed weight or band is noticed.</summary>
public class MatcherScoringTests
{
    private static AlbumMatch Match(string title, string[] artists, int? year = null, string type = "album",
        MusicService service = MusicService.Spotify, string? id = null) =>
        new() { Service = service, Id = id ?? service + ":" + title, Title = title, Artists = artists, Year = year, AlbumType = type };

    // "artaud spinetta" fully explains title and artist, so only the year and the type move the score:
    // 0.4 + 0.25 + 0.25 = 0.9 plus 0.1 times the year score.
    private static double Score(int? queryYear, int? albumYear, string type = "album") =>
        Matcher.ScoreQuery("artaud spinetta", queryYear, Match("Artaud", ["Spinetta"], albumYear, type));

    [Fact]
    public void Without_a_year_in_the_query_the_year_counts_half()
        => Assert.Equal(0.95, Score(null, 1973), 10);

    [Theory]
    [InlineData(1973, 1.0)]   // same year
    [InlineData(1974, 1.0)]   // one off: magazines date by review
    [InlineData(1972, 1.0)]
    [InlineData(1975, 0.95)]  // two or three off: half
    [InlineData(1976, 0.95)]
    [InlineData(1977, 0.9)]   // four or more: nothing, but never a veto
    [InlineData(1900, 0.9)]
    public void The_year_band_moves_the_score_by_up_to_a_tenth(int albumYear, double expected)
        => Assert.Equal(expected, Score(1973, albumYear), 10);

    [Fact]
    public void An_album_with_no_year_counts_half_when_the_query_has_one()
        => Assert.Equal(0.95, Score(1973, null), 10);

    [Theory]
    [InlineData("single", 0.90)]
    [InlineData("SINGLE", 0.90)]
    [InlineData("album", 0.95)]
    [InlineData("compilation", 0.95)]
    public void A_single_scores_a_little_below_the_album(string type, double expected)
        => Assert.Equal(expected, Score(null, 1973, type), 10);

    [Fact]
    public void The_weights_are_title_artist_and_unexplained_words()
    {
        // Naming only the title: title 0.4, no artist, the one typed word is explained (0.25), year unknown (0.05).
        var titleOnly = Matcher.ScoreQuery("artaud", null, Match("Artaud", ["Spinetta"]));
        var artistOnly = Matcher.ScoreQuery("spinetta", null, Match("Artaud", ["Spinetta"]));
        var nothing = Matcher.ScoreQuery("zzzzzz", null, Match("Artaud", ["Spinetta"]));

        Assert.Equal(0.4 + 0.25 * 0 + 0.25 * 1 + 0.05, titleOnly, 10);
        Assert.Equal(0.25 + 0.25 + 0.05, artistOnly, 10);
        Assert.Equal(0.05, nothing, 10);
    }

    [Theory]
    [InlineData("abcd", "abce", true)]            // 4 letters: one typo is fine
    [InlineData("abc", "abd", false)]             // under 4 letters: exact only
    [InlineData("abcd", "abcdef", false)]         // too different in length
    [InlineData("abcdefg", "abcdefx", true)]      // 7 letters: one typo
    [InlineData("abcdefg", "abcdexy", false)]     // 7 letters: two typos are too many
    [InlineData("abcdefgh", "abcdefxy", true)]    // 8 letters or more: two typos
    [InlineData("abcdefgh", "abcdexyz", false)]   // three are too many
    public void Typo_tolerance_has_these_edges(string typed, string title, bool same)
    {
        var score = Matcher.ScoreQuery(typed, null, Match(title, ["Zed"]));

        Assert.Equal(same ? 0.7 : 0.05, score, 10);
    }

    [Fact]
    public void Results_are_best_first_and_at_most_twelve()
    {
        var spotify = Enumerable.Range(0, 15).Select(i => Match("Album " + (char)('a' + i) + "x" + i, ["Artist " + i], 2000 + i)).ToArray();

        var results = Matcher.Combine(spotify, [], "album");

        Assert.Equal(12, results.Count);
        Assert.Equal(results.OrderByDescending(r => r.Score), results);
    }

    [Fact]
    public void The_results_carry_the_scores_Combine_gave_the_matches()
    {
        var spotify = Match("Artaud", ["Spinetta"], 1973);

        var results = Matcher.Combine([spotify], [], "spinetta artaud 1973");

        Assert.Equal(spotify.Score, results[0].Score);
        Assert.True(spotify.Score > 0.9);
    }

    // ───────────── the same album on both services ─────────────

    [Fact]
    public void The_same_title_and_artist_on_both_services_is_one_row_with_both_links()
    {
        var results = Matcher.Combine(
            [Match("Artaud", ["Spinetta"], 1973)],
            [Match("Artaud (Remastered)", ["Spinetta"], 1973, service: MusicService.Tidal)], "spinetta artaud");

        var one = Assert.Single(results);
        Assert.NotNull(one.Spotify);
        Assert.NotNull(one.Tidal);
    }

    [Fact]
    public void A_result_with_no_artist_joins_the_same_title_from_the_other_service()
    {
        var results = Matcher.Combine(
            [Match("Greatest Hits", ["Queen"])],
            [Match("Greatest Hits", [], service: MusicService.Tidal)], "queen greatest hits");

        var one = Assert.Single(results);
        Assert.NotNull(one.Spotify);
        Assert.NotNull(one.Tidal);
    }

    [Fact]
    public void The_same_title_by_different_artists_stays_two_rows()
    {
        var results = Matcher.Combine(
            [Match("Greatest Hits", ["Queen"])],
            [Match("Greatest Hits", ["ABBA"], service: MusicService.Tidal)], "greatest hits");

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void Titles_with_no_letters_or_digits_are_not_merged_by_mistake()
    {
        // Both normalize to nothing, so their keys are equal; they are still different albums.
        var results = Matcher.Combine(
            [Match("???", ["Artist One"])],
            [Match("!!!", [], service: MusicService.Tidal)], "artist");

        Assert.Equal(2, results.Count);
    }
}
