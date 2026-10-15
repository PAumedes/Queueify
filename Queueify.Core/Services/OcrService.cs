using System.Globalization;
using System.Text.RegularExpressions;
using Windows.Foundation;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Queueify.Services;

public sealed record OcrLine(string Text, Rect Bounds);

public sealed record OcrPage(SoftwareBitmap Bitmap, IReadOnlyList<OcrLine> Lines, string LanguageName, bool IsSpanish);

public enum OcrInstallResult
{
    /// <summary>Installed and ready to use.</summary>
    Installed,
    /// <summary>Installed, but this running copy of Queueify can't see it until it restarts.</summary>
    RestartApp,
    /// <summary>Installed; Windows wants a restart to finish.</summary>
    RestartWindows,
    /// <summary>The permission prompt was declined (or no administrator was available). Nothing changed.</summary>
    Cancelled,
    /// <summary>The progress window was closed before the install finished.</summary>
    Interrupted,
    /// <summary>Windows reported an error (no internet, Windows Update blocked by the organization…).</summary>
    Failed,
    /// <summary>Another install started from Queueify is still running.</summary>
    Busy,
    /// <summary>This PC doesn't let Queueify ask for administrator permission (a policy, or PowerShell is blocked).</summary>
    Blocked,
}

/// <summary>What the user can do after an install attempt.</summary>
[Flags]
public enum OcrOffer
{
    None = 0,
    Retry = 1,
    /// <summary>"Don't show this again" on the Photo page.</summary>
    Dismiss = 2,
    Restart = 4,
    /// <summary>Windows' own language settings, the way around when Queueify can't install.</summary>
    WindowsSettings = 8,
}

public sealed record OcrInstallOutcome(OcrInstallResult Result, string? Error = null);

/// <summary>
/// Text recognition with the OCR engine built into Windows (free, offline).
/// Prefers Spanish; falls back to whatever OCR language is installed.
/// </summary>
public static partial class OcrService
{
    private const int ErrorCancelled = 1223;
    private const int ExitInstalled = 0;
    private const int ExitRestartWindows = 2;
    private const int ExitWindowClosed = unchecked((int)0xC000013A);

    private static Task<OcrInstallOutcome>? _install;
    private static readonly HashSet<string> PendingRestart = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The language being installed right now, or null.</summary>
    public static string? InstallingTag { get; private set; }

    /// <summary>Installed during this run but not usable until Queueify restarts.</summary>
    public static IReadOnlyCollection<string> PendingRestartTags => PendingRestart;

    /// <summary>Tags of the OCR languages installed in Windows, e.g. "es-ES", "en-US".</summary>
    public static IReadOnlyList<string> InstalledTags =>
        OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();

    public static bool SpanishAvailable =>
        InstalledTags.Any(t => t.StartsWith("es", StringComparison.OrdinalIgnoreCase));

    public static bool IsInstalled(string tag) =>
        InstalledTags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Installs an OCR language from Windows Update. Needs admin rights, so Windows shows a permission
    /// prompt; a small PowerShell window shows progress. One install at a time.
    /// </summary>
    public static Task<OcrInstallOutcome> InstallAsync(string tag)
    {
        OcrLanguages.CapabilityName(tag); // throws for anything not offered
        if (Precheck(tag, InstalledTags, PendingRestart, _install is { IsCompleted: false }) is { } early)
            return Task.FromResult(new OcrInstallOutcome(early));
        InstallingTag = tag;
        return _install = RunInstallAsync(tag);
    }

    private static async Task<OcrInstallOutcome> RunInstallAsync(string tag)
    {
        var log = Path.Combine(Path.GetTempPath(), $"queueify-ocr-{Guid.NewGuid():N}.log");
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(InstallScript(tag, log)));
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}")
        {
            UseShellExecute = true,
            Verb = "runas",
        };

        try
        {
            // The permission prompt blocks Process.Start until it's answered: keep it off the UI thread.
            using var process = await Task.Run(() => System.Diagnostics.Process.Start(start));
            if (process == null) return new OcrInstallOutcome(OcrInstallResult.Failed);
            await process.WaitForExitAsync();
            var result = ResultOf(process.ExitCode, IsInstalled(tag));
            if (result == OcrInstallResult.RestartApp) PendingRestart.Add(tag);
            return new OcrInstallOutcome(result, result == OcrInstallResult.Failed ? ReadLog(log) : null);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            var result = ResultOfLaunchError(ex.NativeErrorCode);
            return new OcrInstallOutcome(result, result == OcrInstallResult.Cancelled ? null : ex.Message);
        }
        finally
        {
            InstallingTag = null;
            try { File.Delete(log); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string? ReadLog(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() is { Length: > 0 } text ? text : null : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The answer when no install is needed or possible right now: already installed, installed this run
    /// (restart Queueify), or another install running. Null means go ahead and install.
    /// </summary>
    public static OcrInstallResult? Precheck(string tag, IEnumerable<string> installed, IEnumerable<string> pendingRestart, bool busy)
    {
        if (installed.Contains(tag, StringComparer.OrdinalIgnoreCase)) return OcrInstallResult.Installed;
        if (pendingRestart.Contains(tag, StringComparer.OrdinalIgnoreCase)) return OcrInstallResult.RestartApp;
        return busy ? OcrInstallResult.Busy : null;
    }

    /// <summary>Starting the elevated installer failed: declined at the prompt, or not allowed at all.</summary>
    public static OcrInstallResult ResultOfLaunchError(int nativeError) =>
        nativeError == ErrorCancelled ? OcrInstallResult.Cancelled : OcrInstallResult.Blocked;

    /// <summary>What to offer next. Declining or an interrupted install always leaves a way to try again.</summary>
    public static OcrOffer OfferAfter(OcrInstallResult result) => result switch
    {
        OcrInstallResult.Cancelled or OcrInstallResult.Interrupted => OcrOffer.Retry | OcrOffer.Dismiss,
        OcrInstallResult.Failed => OcrOffer.Retry | OcrOffer.Dismiss | OcrOffer.WindowsSettings,
        OcrInstallResult.Blocked => OcrOffer.Dismiss | OcrOffer.WindowsSettings,
        OcrInstallResult.RestartApp => OcrOffer.Restart,
        OcrInstallResult.Busy => OcrOffer.Retry,
        _ => OcrOffer.None,
    };

    /// <summary>What the elevated installer's exit code means.</summary>
    public static OcrInstallResult ResultOf(int exitCode, bool nowAvailable) => exitCode switch
    {
        ExitInstalled => nowAvailable ? OcrInstallResult.Installed : OcrInstallResult.RestartApp,
        ExitRestartWindows => OcrInstallResult.RestartWindows,
        ExitWindowClosed => OcrInstallResult.Interrupted,
        _ => OcrInstallResult.Failed,
    };

    /// <summary>
    /// The script that runs as administrator. Its only inputs are an offered language (checked by
    /// <see cref="OcrLanguages.CapabilityName"/>) and the log path. The path comes from the user's profile
    /// name and may contain any quote character PowerShell knows, so it travels Base64-encoded.
    /// </summary>
    public static string InstallScript(string tag, string logPath)
    {
        var capability = OcrLanguages.CapabilityName(tag);
        var log = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(logPath));
        return $$"""
            $Host.UI.RawUI.WindowTitle = 'Queueify - text recognition'
            $log = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{log}}'))
            Write-Host 'Installing text recognition ({{tag}}) from Windows Update. This can take a minute...'
            try {
                $result = Add-WindowsCapability -Online -Name '{{capability}}' -ErrorAction Stop
                if ($result.RestartNeeded) { exit {{ExitRestartWindows}} }
                exit {{ExitInstalled}}
            } catch {
                Set-Content -LiteralPath $log -Value $_.Exception.Message -Encoding UTF8 -ErrorAction SilentlyContinue
                Write-Host $_ -ForegroundColor Red
                Start-Sleep -Seconds 10
                exit 1
            }
            """;
    }

    /// <param name="preference">"auto" (Spanish when installed) or an installed language tag.</param>
    public static async Task<OcrPage> RecognizeAsync(IRandomAccessStream stream, string preference = "auto")
    {
        var decoder = await BitmapDecoder.CreateAsync(stream);

        // Phone photos are often bigger than the OCR engine accepts. The transform scales in the
        // file's stored orientation and EXIF rotation is applied afterwards, so a uniform scale is safe.
        double max = Math.Min(OcrEngine.MaxImageDimension, 4096);
        double scale = Math.Min(1.0, max / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Round(decoder.PixelWidth * scale),
            ScaledHeight = (uint)Math.Round(decoder.PixelHeight * scale),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);

        var (engine, isSpanish) = CreateEngine(preference);
        var result = await engine.RecognizeAsync(bitmap);

        var lines = new List<OcrLine>();
        foreach (var line in result.Lines)
        {
            if (line.Words.Count == 0) continue;
            double left = line.Words.Min(w => w.BoundingRect.Left);
            double top = line.Words.Min(w => w.BoundingRect.Top);
            double right = line.Words.Max(w => w.BoundingRect.Right);
            double bottom = line.Words.Max(w => w.BoundingRect.Bottom);
            lines.Add(new OcrLine(line.Text, new Rect(left, top, right - left, bottom - top)));
        }

        return new OcrPage(bitmap, lines, OcrLanguages.NameInSentence(engine.RecognizerLanguage.LanguageTag), isSpanish);
    }

    private static (OcrEngine Engine, bool IsSpanish) CreateEngine(string preference)
    {
        if (OcrLanguages.Choose(preference, InstalledTags) is string tag
            && OcrEngine.TryCreateFromLanguage(new Language(tag)) is OcrEngine chosen)
            return (chosen, IsSpanishTag(chosen.RecognizerLanguage.LanguageTag));

        var fallback = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? OcrEngine.AvailableRecognizerLanguages.Select(OcrEngine.TryCreateFromLanguage).FirstOrDefault(e => e != null)
            ?? throw new InvalidOperationException(Loc.T("ocr.none_in_windows"));
        return (fallback, IsSpanishTag(fallback.RecognizerLanguage.LanguageTag));
    }

    private static bool IsSpanishTag(string tag) => tag.StartsWith("es", StringComparison.OrdinalIgnoreCase);
}
