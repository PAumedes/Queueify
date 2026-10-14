using Queueify.Services;

namespace Queueify.Web.Ocr;

/// <summary>
/// Reads one uploaded photo: admission, check and re-encode, the engine, a time limit. The photo only
/// ever exists in memory, as the bytes of the upload and then of the clean PNG, both wiped afterwards;
/// nothing is written to disk.
/// </summary>
public sealed class PhotoReader(IOcr ocr, OcrGate gate, OcrOptions options)
{
    public const int MaxLines = 300;
    public const int MaxLineLength = 300;

    public async Task<OcrReading> ReadAsync(string userId, Stream upload, string language, CancellationToken ct)
    {
        using var slot = gate.TryEnter(userId) ?? throw new PhotoException(PhotoProblem.Busy);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(options.Timeout);

        PreparedImage? prepared = null;
        try
        {
            prepared = await Task.Run(() => ImageGuard.Prepare(upload, options), limit.Token);
            using var png = new MemoryStream(prepared.Png, writable: false);
            var reading = await ocr.RecognizeAsync(png, language, limit.Token);
            return Trim(reading);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new PhotoException(PhotoProblem.Timeout);
        }
        finally
        {
            if (prepared is not null) Array.Clear(prepared.Png);
        }
    }

    /// <summary>The engine's answer is data too: bounded in count and length before it reaches a page.</summary>
    private static OcrReading Trim(OcrReading reading) => reading with
    {
        Lines = reading.Lines
            .Take(MaxLines)
            .Select(l => l.Text.Length > MaxLineLength ? l with { Text = l.Text[..MaxLineLength] } : l)
            .ToList(),
    };
}
