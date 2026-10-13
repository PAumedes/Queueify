using Queueify.Services;

namespace Queueify.Tests;

public class OcrLanguagesTests
{
    [Fact]
    public void Offered_StartsWithSpanish_AndCoversTheMagazineLanguages()
    {
        Assert.Equal("es-ES", OcrLanguages.Offered[0]);
        foreach (var tag in new[] { "es-MX", "en-US", "pt-BR", "fr-FR", "it-IT", "de-DE" })
            Assert.Contains(tag, OcrLanguages.Offered);
    }

    [Fact]
    public void CapabilityName_IsTheWindowsFeatureOnDemand()
    {
        Assert.Equal("Language.OCR~~~es-ES~0.0.1.0", OcrLanguages.CapabilityName("es-ES"));
    }

    [Theory]
    [InlineData("xx-XX")]
    [InlineData("es-ES'; Remove-Item C:\\ -Recurse; '")]
    [InlineData("")]
    public void CapabilityName_RefusesAnythingNotOffered(string tag)
    {
        // The name goes into an elevated PowerShell script: only known values may get there.
        Assert.Throws<ArgumentException>(() => OcrLanguages.CapabilityName(tag));
    }

    [Fact]
    public void Installable_LeavesOutWhatIsInstalled_IgnoringCase()
    {
        var installable = OcrLanguages.Installable(["ES-es", "en-US"]);

        Assert.DoesNotContain(installable, o => o.Tag == "es-ES");
        Assert.DoesNotContain(installable, o => o.Tag == "en-US");
        Assert.Contains(installable, o => o.Tag == "pt-BR");
    }

    [Fact]
    public void Names_AreReadableInTheirOwnLanguage()
    {
        var option = OcrLanguages.Installable([]).Single(o => o.Tag == "es-ES");
        Assert.Equal("Español (España)", option.Name);
    }

    [Theory]
    [InlineData("auto", new[] { "en-US", "es-ES" }, "es-ES")]   // automatic: Spanish when it's there
    [InlineData("auto", new[] { "en-US", "es-MX" }, "es-MX")]
    [InlineData("auto", new[] { "en-US" }, null)]              // no Spanish: Windows' own choice
    [InlineData("pt-BR", new[] { "en-US", "pt-BR", "es-ES" }, "pt-BR")]
    [InlineData("pt-BR", new[] { "en-US", "es-ES" }, "es-ES")] // chosen language was removed: back to automatic
    public void Choose_PicksTheLanguageThatReadsThePhoto(string preference, string[] installed, string? expected)
    {
        Assert.Equal(expected, OcrLanguages.Choose(preference, installed));
    }

    [Theory]
    [InlineData(0, true, OcrInstallResult.Installed)]
    [InlineData(0, false, OcrInstallResult.RestartApp)]          // installed, but this process can't see it yet
    [InlineData(2, false, OcrInstallResult.RestartWindows)]
    [InlineData(1, false, OcrInstallResult.Failed)]
    [InlineData(-1073741510, false, OcrInstallResult.Interrupted)] // the progress window was closed
    public void ResultOf_TheInstallScriptsExitCode(int exitCode, bool nowAvailable, OcrInstallResult expected)
    {
        Assert.Equal(expected, OcrService.ResultOf(exitCode, nowAvailable));
    }

    [Fact]
    public void InstallScript_InstallsOnlyTheRequestedFeature_AndReportsErrorsToTheLog()
    {
        var script = OcrService.InstallScript("pt-BR", @"C:\Temp\log.txt");

        Assert.Contains("Add-WindowsCapability -Online -Name 'Language.OCR~~~pt-BR~0.0.1.0'", script);
        Assert.Contains(@"C:\Temp\log.txt", script);
        Assert.Throws<ArgumentException>(() => OcrService.InstallScript("xx-XX", @"C:\Temp\log.txt"));
    }
}
