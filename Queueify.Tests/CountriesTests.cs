using System.Globalization;
using Queueify.Services;

namespace Queueify.Tests;

public class CountriesTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-AR");

    [Fact]
    public void Codes_AreTheCountriesWhereBothServicesWork()
    {
        Assert.Equal(61, Countries.Codes.Count);
        Assert.Contains("AR", Countries.Codes);
        Assert.Contains("US", Countries.Codes);
        Assert.DoesNotContain("IN", Countries.Codes);   // Spotify yes, Tidal no
        Assert.All(Countries.Codes, c => Assert.Matches("^[A-Z]{2}$", c));
        Assert.Equal(Countries.Codes.Count, Countries.Codes.Distinct().Count());
    }

    [Fact]
    public void Options_HaveReadableNamesSortedForTheReader()
    {
        var options = Countries.Options("AR", English);

        Assert.Contains(options, o => o.Code == "AR" && o.Name == "Argentina");
        Assert.Equal(options.Select(o => o.Name).Order(StringComparer.Create(English, true)), options.Select(o => o.Name));
    }

    [Fact]
    public void Options_UseTheLanguageOfTheApp()
    {
        var options = Countries.Options("AR", Spanish);

        Assert.Contains(options, o => o.Code == "DE" && o.Name == "Alemania");
    }

    [Fact]
    public void Options_KeepASavedCountryThatIsNotInTheList()
    {
        var options = Countries.Options("IN", English);

        Assert.Contains(options, o => o.Code == "IN");
        Assert.Equal(62, options.Count);
    }

    [Fact]
    public void Options_IgnoreAnInvalidSavedCode()
    {
        Assert.Equal(61, Countries.Options("??", English).Count);
        Assert.Equal(61, Countries.Options("", English).Count);
    }
}
