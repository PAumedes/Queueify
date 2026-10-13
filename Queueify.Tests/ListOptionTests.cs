using Queueify.Services;

namespace Queueify.Tests;

// Settings lists show these through a template; screen readers read each item by its ToString().
public class ListOptionTests
{
    [Fact]
    public void A_country_reads_as_its_name()
    {
        Assert.Equal("Argentina", new CountryOption("AR", "Argentina").ToString());
    }

    [Fact]
    public void A_text_recognition_language_reads_as_its_name()
    {
        Assert.Equal("Spanish (Spain)", new OcrLanguageOption("es-ES", "Spanish (Spain)").ToString());
    }
}
