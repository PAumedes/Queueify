using Queueify.Services;

namespace Queueify.Web.Tests.Ocr;

/// <summary>The line cleanup is plain logic shared with the desktop app; it must exist on plain net10.0 too.</summary>
public class CleanLineTests
{
    [Theory]
    [InlineData("CHARLY GARCÍA", "Charly García", null)]
    [InlineData("Clics modernos (Interdisc, 1983)", "Clics modernos", 1983)]
    [InlineData("«Bajo Belgrano»", "Bajo Belgrano", null)]
    [InlineData("Año: 1983 - sello: EMI", "Año: 1983 - sello: EMI", 1983)]
    public void CleanLine_works_on_the_web_build(string raw, string text, int? year) =>
        Assert.Equal((text, year), OcrService.CleanLine(raw));
}
