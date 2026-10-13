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
        var log = @"C:\Temp\log.txt";
        var script = OcrService.InstallScript("pt-BR", log);

        Assert.Contains("Add-WindowsCapability -Online -Name 'Language.OCR~~~pt-BR~0.0.1.0'", script);
        Assert.Contains(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(log)), script);
        Assert.Throws<ArgumentException>(() => OcrService.InstallScript("xx-XX", log));
    }

    [Theory]
    [InlineData(@"C:\Users\O'Brien\AppData\Local\Temp\q.log")]
    [InlineData("C:\\Users\\O\u2019Brien\\AppData\\Local\\Temp\\q.log")]   // PowerShell also ends strings at ’ ‘ ‚ ‛
    [InlineData("C:\\Users\\X\u2019; Write-Host PWNED; \u2019x\\q.log")]
    public void InstallScript_NeverPutsThePathItselfIntoTheScript(string log)
    {
        // The script runs as administrator; the path comes from the user's profile name. Encoded, it
        // can't end a string early or add commands, whatever quote characters it contains.
        var script = OcrService.InstallScript("es-ES", log);

        Assert.DoesNotContain(log, script);
        Assert.DoesNotContain("PWNED", script);
        Assert.DoesNotContain("\u2019", script);
        Assert.Equal("0", PowerShellParseErrors(script));
    }

    /// <summary>Asks Windows PowerShell's own parser how many syntax errors the script has.</summary>
    private static string PowerShellParseErrors(string script)
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var check = "$s = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('" + encoded + "')); " +
                    "$t = $null; $e = $null; [void][System.Management.Automation.Language.Parser]::ParseInput($s, [ref]$t, [ref]$e); $e.Count";
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(check)))
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return output;
    }

    [Theory]
    [InlineData(OcrInstallResult.Cancelled, OcrOffer.Retry | OcrOffer.Dismiss)]
    [InlineData(OcrInstallResult.Interrupted, OcrOffer.Retry | OcrOffer.Dismiss)]
    [InlineData(OcrInstallResult.Failed, OcrOffer.Retry | OcrOffer.Dismiss | OcrOffer.WindowsSettings)]
    [InlineData(OcrInstallResult.Blocked, OcrOffer.Dismiss | OcrOffer.WindowsSettings)]
    [InlineData(OcrInstallResult.RestartApp, OcrOffer.Restart)]
    [InlineData(OcrInstallResult.Installed, OcrOffer.None)]
    [InlineData(OcrInstallResult.RestartWindows, OcrOffer.None)]
    [InlineData(OcrInstallResult.Busy, OcrOffer.Retry)]               // the other install may finish; try again then
    public void OfferAfter_SaysWhatTheUserCanDoNext(OcrInstallResult result, OcrOffer expected)
    {
        // Declining the permission prompt must leave the offer to try again.
        Assert.Equal(expected, OcrService.OfferAfter(result));
    }

    [Theory]
    [InlineData(1223, OcrInstallResult.Cancelled)]   // the prompt was declined
    [InlineData(5, OcrInstallResult.Blocked)]        // a policy denies elevation outright
    [InlineData(1260, OcrInstallResult.Blocked)]     // powershell.exe blocked (AppLocker / SRP)
    public void ResultOfLaunchError_TellsDecliningFromBeingBlocked(int nativeError, OcrInstallResult expected)
    {
        Assert.Equal(expected, OcrService.ResultOfLaunchError(nativeError));
    }

    [Theory]
    [InlineData(@"C:\Users\O'Brien\AppData\Local\Temp\q.log")]
    [InlineData("C:\\Users\\O\u2019Brien\\AppData\\Local\\Temp\\q.log")]
    public void InstallScript_TheLogPathDecodesBackToTheSamePath(string log)
    {
        // Runs only the script's $log line (nothing elevated) and prints the result.
        var line = OcrService.InstallScript("es-ES", log).Split('\n').Single(l => l.TrimStart().StartsWith("$log ="));
        var check = line + "; [Console]::OutputEncoding = [Text.Encoding]::UTF8; [Console]::Out.Write($log)";
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(check)))
        {
            RedirectStandardOutput = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
        };
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        Assert.Equal(log, output);
    }

    [Fact]
    public async Task InstallAsync_ALanguageWaitingForARestart_IsNotInstalledAgain()
    {
        // Installed earlier in this run: no second permission prompt, just "restart Queueify".
        OcrService.MarkPendingRestart("pt-PT");

        var outcome = await OcrService.InstallAsync("pt-PT");

        Assert.Equal(OcrInstallResult.RestartApp, outcome.Result);
        Assert.Contains("pt-PT", OcrService.PendingRestartTags);
    }

    [Fact]
    public void PendingRestart_CountsAsInstalledWhenOffering()
    {
        var installable = OcrLanguages.Installable(["en-US"], pendingRestart: ["pt-BR"]);
        Assert.DoesNotContain(installable, o => o.Tag == "pt-BR");
    }
}
