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
        if (IsInstalled(tag)) return Task.FromResult(new OcrInstallOutcome(OcrInstallResult.Installed));
        if (_install is { IsCompleted: false }) return Task.FromResult(new OcrInstallOutcome(OcrInstallResult.Busy));
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
            using var process = System.Diagnostics.Process.Start(start);
            if (process == null) return new OcrInstallOutcome(OcrInstallResult.Failed);
            await process.WaitForExitAsync();
            var result = ResultOf(process.ExitCode, IsInstalled(tag));
            return new OcrInstallOutcome(result, result == OcrInstallResult.Failed ? ReadLog(log) : null);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new OcrInstallOutcome(OcrInstallResult.Cancelled);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new OcrInstallOutcome(OcrInstallResult.Failed, ex.Message);
        }
        finally
        {
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
    /// <see cref="OcrLanguages.CapabilityName"/>) and the log path, quoted for PowerShell.
    /// </summary>
    public static string InstallScript(string tag, string logPath)
    {
        var capability = OcrLanguages.CapabilityName(tag);
        var log = logPath.Replace("'", "''");
        return $$"""
            $Host.UI.RawUI.WindowTitle = 'Queueify - text recognition'
            Write-Host 'Installing text recognition ({{tag}}) from Windows Update. This can take a minute...'
            try {
                $result = Add-WindowsCapability -Online -Name '{{capability}}' -ErrorAction Stop
                if ($result.RestartNeeded) { exit {{ExitRestartWindows}} }
                exit {{ExitInstalled}}
            } catch {
                Set-Content -LiteralPath '{{log}}' -Value $_.Exception.Message -Encoding UTF8 -ErrorAction SilentlyContinue
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

        return new OcrPage(bitmap, lines, engine.RecognizerLanguage.DisplayName, isSpanish);
    }

    private static (OcrEngine Engine, bool IsSpanish) CreateEngine(string preference)
    {
        if (OcrLanguages.Choose(preference, InstalledTags) is string tag
            && OcrEngine.TryCreateFromLanguage(new Language(tag)) is OcrEngine chosen)
            return (chosen, IsSpanishTag(chosen.RecognizerLanguage.LanguageTag));

        var fallback = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? OcrEngine.AvailableRecognizerLanguages.Select(OcrEngine.TryCreateFromLanguage).FirstOrDefault(e => e != null)
            ?? throw new InvalidOperationException("No OCR language is installed in Windows.");
        return (fallback, IsSpanishTag(fallback.RecognizerLanguage.LanguageTag));
    }

    private static bool IsSpanishTag(string tag) => tag.StartsWith("es", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Cleans a clicked OCR line: pulls out a release year, drops "(Label, 1983)"-style notes and
    /// stray quotes, and turns ALL-CAPS magazine headings into Title Case.
    /// </summary>
    public static (string Text, int? Year) CleanLine(string raw)
    {
        int? year = null;
        var ym = YearPattern().Match(raw);
        if (ym.Success) year = int.Parse(ym.Value, CultureInfo.InvariantCulture);

        var text = BracketWithYear().Replace(raw, " ");
        text = text.Trim().Trim('"', '“', '”', '«', '»', '\'', '‘', '’', '-', '–', '—', '•', '·', ':', ',', '.', ' ');
        text = Whitespace().Replace(text, " ");

        // "Mostly uppercase" rather than "all": OCR often turns one capital (e.g. Í) into a lowercase letter.
        var letters = text.Where(char.IsLetter).ToList();
        if (letters.Count >= 3 && letters.Count(char.IsUpper) >= letters.Count * 0.8)
            text =CultureInfo.GetCultureInfo("es-AR").TextInfo.ToTitleCase(text.ToLower(CultureInfo.GetCultureInfo("es-AR")));

        return (text, year);
    }

    [GeneratedRegex(@"\b(19[4-9]\d|20[0-4]\d)\b")]
    private static partial Regex YearPattern();

    [GeneratedRegex(@"[\(\[][^\)\]]*\b(19[4-9]\d|20[0-4]\d)\b[^\)\]]*[\)\]]")]
    private static partial Regex BracketWithYear();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
