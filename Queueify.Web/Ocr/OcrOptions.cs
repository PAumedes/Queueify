namespace Queueify.Web.Ocr;

/// <summary>Limits and locations for reading magazine photos; the "Ocr" configuration section.</summary>
public sealed class OcrOptions
{
    /// <summary>The biggest upload accepted, in bytes (a phone photo is 2 to 8 MB).</summary>
    public long MaxBytes { get; set; } = 12 * 1024 * 1024;

    /// <summary>The most pixels an upload may hold: a decompression-bomb guard, checked before decoding.</summary>
    public long MaxPixels { get; set; } = 60_000_000;

    /// <summary>The photo is scaled down to this many pixels on its longest side before reading.</summary>
    public int MaxSide { get; set; } = 3000;

    /// <summary>How long one photo may take before the reader is stopped.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(40);

    /// <summary>Photos being read at the same time on the whole server, across users.</summary>
    public int MaxConcurrent { get; set; } = 2;

    /// <summary>The tesseract program: a full path, or a name found on PATH.</summary>
    public string TesseractPath { get; set; } = "tesseract";

    /// <summary>
    /// A folder of *.traineddata that an administrator manages. Empty means Queueify keeps its own copy
    /// under the data folder and downloads the two languages once, checking their checksums.
    /// </summary>
    public string TessdataPath { get; set; } = "";
}
