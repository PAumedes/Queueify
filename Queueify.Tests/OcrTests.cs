using Queueify.Services;
using Windows.Storage;

namespace Queueify.Tests;

public class OcrTests
{
    [Theory]
    [InlineData("CHARLY GARCÍA", "Charly García", null)]
    [InlineData("CHARLY GARCiA", "Charly Garcia", null)]             // one capital misread as lowercase
    [InlineData("Clics modernos (Interdisc, 1983)", "Clics modernos", 1983)]
    [InlineData("«Bajo Belgrano»", "Bajo Belgrano", null)]
    [InlineData("SPINETTA JADE", "Spinetta Jade", null)]
    [InlineData("Año: 1983 - sello: EMI", "Año: 1983 - sello: EMI", 1983)]
    public void CleanLine_TidiesAClickedLine(string raw, string text, int? year)
    {
        Assert.Equal((text, year), OcrService.CleanLine(raw));
    }

    [Fact]
    public async Task RecognizeAsync_ReadsTheLinesOfAMagazinePage()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "magazine-page.png");
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenAsync(FileAccessMode.Read);

        var page = await OcrService.RecognizeAsync(stream);

        var lines = page.Lines.Select(l => l.Text).ToList();
        Assert.Contains("Clics modernos (Interdisc, 1983)", lines);
        Assert.Contains("SPINETTA JADE", lines);
        Assert.All(page.Lines, l => Assert.True(l.Bounds.Width > 0 && l.Bounds.Height > 0));
        if (page.IsSpanish) Assert.Contains("Año: 1983 - sello: EMI", lines);
    }
}
