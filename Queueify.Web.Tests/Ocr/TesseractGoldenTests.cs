using Microsoft.Extensions.Logging.Abstractions;
using Queueify.Services;
using Queueify.Web.Ocr;

namespace Queueify.Web.Tests.Ocr;

/// <summary>
/// The real engine on the same magazine page the desktop test reads. Needs the tesseract program and the
/// models offline: QUEUEIFY_TESSERACT is the program, QUEUEIFY_TESSDATA the folder with spa and eng.
/// </summary>
public class TesseractGoldenTests
{
    [Fact]
    public async Task Reads_the_lines_of_the_magazine_page_with_their_boxes()
    {
        var exe = Environment.GetEnvironmentVariable("QUEUEIFY_TESSERACT");
        var data = Environment.GetEnvironmentVariable("QUEUEIFY_TESSDATA");
        Assert.SkipWhen(exe is not { Length: > 0 } || !File.Exists(exe) || data is not { Length: > 0 } || !File.Exists(Path.Combine(data, "spa.traineddata")),
            "Set QUEUEIFY_TESSERACT to the tesseract program and QUEUEIFY_TESSDATA to a folder with spa.traineddata and eng.traineddata.");

        var options = new OcrOptions { TesseractPath = exe!, TessdataPath = data! };
        var ocr = new TesseractOcr(options, new TessdataFetcher(new HttpClient(), "unused", data!), NullLogger<TesseractOcr>.Instance);
        var reader = new PhotoReader(ocr, new OcrGate(options), options);
        await using var photo = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "TestData", "magazine-page.png"));

        var reading = await reader.ReadAsync("golden", photo, "auto", TestContext.Current.CancellationToken);

        var lines = reading.Lines.Select(l => l.Text).ToList();
        Assert.Contains("Clics modernos (Interdisc, 1983)", lines);
        Assert.Contains("SPINETTA JADE", lines);
        Assert.Contains("Año: 1983 - Sello: EMI", lines);
        Assert.Equal((1600, 1000), (reading.Width, reading.Height));
        Assert.All(reading.Lines, l => Assert.True(l.Bounds.Width > 0 && l.Bounds.Height > 0 && l.Bounds.X + l.Bounds.Width <= 1600));
        Assert.Equal(("Charly García", (int?)null), OcrService.CleanLine(lines.First(l => l.StartsWith("CHARLY"))));
    }
}
