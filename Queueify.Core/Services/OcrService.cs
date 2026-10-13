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

public enum OcrInstallResult { Installed, RestartApp, Cancelled, Failed }

/// <summary>
/// Text recognition with the OCR engine built into Windows (free, offline).
/// Prefers Spanish; falls back to whatever OCR language is installed.
/// </summary>
public static partial class OcrService
{
    private const string SpanishCapability = "Language.OCR~~~es-ES~0.0.1.0";
    private const int ErrorCancelled = 1223;

    public static bool SpanishAvailable =>
        OcrEngine.AvailableRecognizerLanguages.Any(l => l.LanguageTag.StartsWith("es", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Installs the Spanish OCR component from Windows Update. Needs admin rights, so Windows shows
    /// a UAC prompt; a small PowerShell window shows progress and any error.
    /// </summary>
    public static async Task<OcrInstallResult> InstallSpanishAsync()
    {
        var script = $$"""
            $Host.UI.RawUI.WindowTitle = 'Queueify - Spanish text recognition'
            Write-Host 'Installing Spanish text recognition from Windows Update. This can take a minute...'
            try {
                Add-WindowsCapability -Online -Name '{{SpanishCapability}}' -ErrorAction Stop | Out-Null
                exit 0
            } catch {
                Write-Host $_ -ForegroundColor Red
                Start-Sleep -Seconds 15
                exit 1
            }
            """;
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}")
        {
            UseShellExecute = true,
            Verb = "runas",
        };

        System.Diagnostics.Process? process;
        try
        {
            process = System.Diagnostics.Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return OcrInstallResult.Cancelled;
        }
        if (process == null) return OcrInstallResult.Failed;

        using (process)
        {
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) return OcrInstallResult.Failed;
        }
        return SpanishAvailable ? OcrInstallResult.Installed : OcrInstallResult.RestartApp;
    }

    public static async Task<OcrPage> RecognizeAsync(IRandomAccessStream stream)
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

        var (engine, isSpanish) = CreateEngine();
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

    private static (OcrEngine Engine, bool IsSpanish) CreateEngine()
    {
        var spanish = OcrEngine.AvailableRecognizerLanguages
            .FirstOrDefault(l => l.LanguageTag.StartsWith("es", StringComparison.OrdinalIgnoreCase));
        if (spanish != null && OcrEngine.TryCreateFromLanguage(spanish) is OcrEngine es)
            return (es, true);

        var fallback = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? OcrEngine.AvailableRecognizerLanguages.Select(OcrEngine.TryCreateFromLanguage).FirstOrDefault(e => e != null)
            ?? throw new InvalidOperationException("No OCR language is installed in Windows.");
        return (fallback, false);
    }

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
