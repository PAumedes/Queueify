using Queueify.Services;

namespace Queueify.Tests;

/// <summary>The one rule for which release years are believed, shared by the PC app, the sync and the web server.</summary>
public class AlbumYearTests
{
    [Theory]
    [InlineData(1000, true)]
    [InlineData(1973, true)]
    [InlineData(3000, true)]
    [InlineData(999, false)]
    [InlineData(3001, false)]
    [InlineData(0, false)]
    [InlineData(-1973, false)]
    [InlineData(int.MaxValue, false)]
    public void Years_between_1000_and_3000_are_believed(int year, bool believed)
    {
        Assert.Equal(believed, AlbumYear.IsValid(year));
        Assert.Equal(believed ? year : (int?)null, AlbumYear.OrNull(year));
    }

    [Fact]
    public void No_year_stays_no_year() => Assert.Null(AlbumYear.OrNull((int?)null));

    [Fact]
    public void A_long_from_a_database_is_bounded_before_it_is_narrowed()
    {
        Assert.Equal(1983, AlbumYear.OrNull(1983L));
        Assert.Null(AlbumYear.OrNull(4_294_969_279L));   // would wrap to 1983 as an int
    }
}
