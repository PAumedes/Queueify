namespace Queueify.Services;

/// <summary>A box in the pixels of the recognized image: plain numbers, so it needs no Windows type.</summary>
public readonly record struct OcrBox(double X, double Y, double Width, double Height);

public sealed record OcrTextLine(string Text, OcrBox Bounds);

/// <summary>What was read from one photo, and the size of the image the boxes refer to.</summary>
public sealed record OcrReading(IReadOnlyList<OcrTextLine> Lines, int Width, int Height, string Language);

/// <summary>
/// Reads the text on a picture. The desktop app answers with Windows OCR, the web app with Tesseract.
/// </summary>
public interface IOcr
{
    /// <param name="image">An encoded image (JPEG, PNG…); the caller keeps ownership of the stream.</param>
    /// <param name="language">"auto" (Spanish when available) or a primary language tag such as "es" or "en".</param>
    Task<OcrReading> RecognizeAsync(Stream image, string language, CancellationToken ct = default);
}
