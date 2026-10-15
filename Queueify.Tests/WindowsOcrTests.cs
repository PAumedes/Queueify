using Queueify.Services;

namespace Queueify.Tests;

public class WindowsOcrTests
{
    [Theory]
    [InlineData("en", "en-US")]
    [InlineData("es", "es-ES")]
    [InlineData("auto", "auto")]            // left for OcrLanguages.Choose
    [InlineData("fr", "fr")]                // not installed: unchanged
    [InlineData("en-GB", "en-GB")]          // already a full tag
    public void PreferenceFor_TurnsAPrimaryTagIntoAnInstalledOne(string language, string expected) =>
        Assert.Equal(expected, WindowsOcr.PreferenceFor(language, ["es-ES", "en-US"]));

    [Fact]
    public async Task RecognizeAsync_ReadsThePageThroughTheSharedInterface()
    {
        Assert.SkipWhen(OcrService.InstalledTags.Count == 0, "No text recognition language is installed in Windows.");

        IOcr ocr = new WindowsOcr();
        await using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "TestData", "magazine-page.png"));

        var reading = await ocr.RecognizeAsync(file, "auto", TestContext.Current.CancellationToken);

        Assert.Contains(reading.Lines, l => l.Text == "SPINETTA JADE");
        Assert.All(reading.Lines, l => Assert.True(l.Bounds.Width > 0 && l.Bounds.Height > 0));
        Assert.True(reading.Width > 0 && reading.Height > 0);
    }
}
