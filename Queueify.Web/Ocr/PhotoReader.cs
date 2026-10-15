using Queueify.Services;

namespace Queueify.Web.Ocr;

/// <summary>
/// Reads one uploaded photo: admission first (so a turned-away upload is never read into memory), then the
/// check and re-encode, the engine, and a time limit. The photo only exists in memory, as the bytes of the
/// upload and then of the clean PNG; nothing is written to disk and the clean PNG's buffer is wiped afterwards.
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
            using var bytes = await BufferAsync(upload, options.MaxBytes, limit.Token);
            // Decoding can't be interrupted, so the slot is kept until it ends: the "one at a time" bound holds even when it is slow.
            prepared = await Task.Run(() => ImageGuard.Prepare(bytes, options), CancellationToken.None);
            limit.Token.ThrowIfCancellationRequested();
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

    private static async Task<MemoryStream> BufferAsync(Stream body, long max, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + n > max) throw new PhotoException(PhotoProblem.TooLarge);
            buffer.Write(chunk, 0, n);
        }
        buffer.Position = 0;
        return buffer;
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
