using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Queueify.Services;

namespace Queueify.Web.Ocr;

/// <summary>
/// <see cref="IOcr"/> with the tesseract program, run as a child process: the picture goes in through stdin
/// and the answer (TSV, one row per word with its box) comes out of stdout, so no file is ever written. A
/// separate process can be killed on a timeout and can't take the web server down with it.
/// </summary>
public sealed class TesseractOcr(OcrOptions options, TessdataFetcher tessdata, ILogger<TesseractOcr> log) : IOcr
{
    private const int MaxOutputChars = 4_000_000;

    public async Task<OcrReading> RecognizeAsync(Stream image, string language, CancellationToken ct = default)
    {
        var languages = LanguagesFor(language);
        // The first download has its own (HTTP) time limit: under the photo's 40 s it would restart from zero every time.
        var folder = await tessdata.EnsureAsync(CancellationToken.None);

        var start = new ProcessStartInfo(options.TesseractPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in ArgumentsFor(languages, folder)) start.ArgumentList.Add(arg);
        start.Environment["OMP_THREAD_LIMIT"] = "1";   // one core per photo: several users share the server

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new PhotoException(PhotoProblem.EngineMissing);
        }
        catch (Win32Exception ex)
        {
            log.LogError("Can't start {Program}: {Message}", options.TesseractPath, ex.Message);
            throw new PhotoException(PhotoProblem.EngineMissing);
        }

        using (process)
        using (ct.Register(() => TryKill(process)))
        {
            var errors = process.StandardError.ReadToEndAsync(ct);
            var output = ReadCappedAsync(process.StandardOutput, ct);
            try
            {
                await image.CopyToAsync(process.StandardInput.BaseStream, ct);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The engine closed its input early; its exit code below says why.
            }

            var tsv = await output;
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0)
            {
                log.LogError("tesseract exited with {Code}: {Errors}", process.ExitCode, Truncate(await errors));
                throw new PhotoException(PhotoProblem.EngineMissing);
            }
            return ParseTsv(tsv, NameOf(languages));
        }
    }

    /// <summary>"auto", "es" or "en", and nothing else: the value ends up on a command line.</summary>
    public static string LanguagesFor(string choice) => choice switch
    {
        "auto" => "spa+eng",
        "es" => "spa",
        "en" => "eng",
        _ => throw new ArgumentException("Not an offered language.", nameof(choice)),
    };

    public static IReadOnlyList<string> ArgumentsFor(string languages, string tessdataFolder) =>
        ["stdin", "stdout", "--tessdata-dir", tessdataFolder, "-l", languages, "-c", "tessedit_create_tsv=1"];

    /// <summary>Lines (level 4 rows, with their box) and their words (level 5), from tesseract's TSV.</summary>
    public static OcrReading ParseTsv(string tsv, string language)
    {
        int width = 0, height = 0;
        var lines = new List<OcrTextLine>();
        OcrBox? box = null;
        var words = new List<string>();

        void Finish()
        {
            if (box is { } b && words.Count > 0) lines.Add(new OcrTextLine(string.Join(' ', words), b));
            box = null;
            words.Clear();
        }

        foreach (var row in tsv.Split('\n'))
        {
            var cells = row.TrimEnd('\r').Split('\t');
            if (cells.Length < 11 || !int.TryParse(cells[0], out var level)) continue;
            switch (level)
            {
                case 1 when TryBox(cells, out var page):
                    width = (int)page.Width;
                    height = (int)page.Height;
                    break;
                case 4:
                    Finish();   // a line whose box can't be read is dropped with its words, not merged into the previous one
                    if (TryBox(cells, out var line)) box = line;
                    break;
                case 5 when cells.Length >= 12 && box is not null
                            && double.TryParse(cells[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var conf) && conf >= 0
                            && cells[11].Trim() is { Length: > 0 } word:
                    words.Add(word);
                    break;
            }
        }
        Finish();
        return new OcrReading(lines, width, height, language);
    }

    private static bool TryBox(string[] cells, out OcrBox box)
    {
        var ok = double.TryParse(cells[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                 & double.TryParse(cells[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                 & double.TryParse(cells[8], NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
                 & double.TryParse(cells[9], NumberStyles.Float, CultureInfo.InvariantCulture, out var h);
        box = new OcrBox(x, y, w, h);
        return ok;
    }

    private static string NameOf(string languages) => languages switch
    {
        "spa" => "Español",
        "eng" => "English",
        _ => "Español + English",
    };

    private static async Task<string> ReadCappedAsync(StreamReader reader, CancellationToken ct)
    {
        var text = new StringBuilder();
        var buffer = new char[8192];
        int n;
        while ((n = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            // Past the cap the rest is read and thrown away, so the engine never blocks on a full pipe.
            if (text.Length + n <= MaxOutputChars) text.Append(buffer, 0, n);
        }
        return text.ToString();
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
    }

    private static string Truncate(string s) => s.Length > 500 ? s[..500] : s;
}
