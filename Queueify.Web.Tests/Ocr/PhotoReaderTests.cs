using Queueify.Services;
using Queueify.Web.Ocr;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Queueify.Web.Tests.Ocr;

public class PhotoReaderTests
{
    private sealed class FakeOcr(Func<Stream, string, CancellationToken, Task<OcrReading>>? run = null) : IOcr
    {
        public List<(byte[] Bytes, string Language)> Calls { get; } = [];
        public async Task<OcrReading> RecognizeAsync(Stream image, string language, CancellationToken ct = default)
        {
            using var copy = new MemoryStream();
            await image.CopyToAsync(copy, ct);
            Calls.Add((copy.ToArray(), language));
            if (run is not null) return await run(image, language, ct);
            return new OcrReading([new OcrTextLine("SPINETTA JADE", new OcrBox(1, 2, 3, 4))], 20, 10, "Español");
        }
    }

    private static MemoryStream Photo(int w = 20, int h = 10)
    {
        using var image = new Image<Rgb24>(w, h);
        var ms = new MemoryStream();
        image.SaveAsPng(ms);
        ms.Position = 0;
        return ms;
    }

    private static PhotoReader Reader(IOcr ocr, OcrOptions? options = null) =>
        new(ocr, new OcrGate(options ?? new OcrOptions()), options ?? new OcrOptions());

    [Fact]
    public async Task The_engine_gets_the_clean_png_and_the_language_the_user_chose()
    {
        var ocr = new FakeOcr();
        var reading = await Reader(ocr).ReadAsync("alice", Photo(), "es", TestContext.Current.CancellationToken);

        var call = Assert.Single(ocr.Calls);
        Assert.Equal("es", call.Language);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, call.Bytes[..4]);
        Assert.Equal("SPINETTA JADE", Assert.Single(reading.Lines).Text);
    }

    [Fact]
    public async Task A_file_that_is_not_a_picture_never_reaches_the_engine()
    {
        var ocr = new FakeOcr();
        await Assert.ThrowsAsync<PhotoException>(() => Reader(ocr).ReadAsync("alice", new MemoryStream("hello"u8.ToArray()), "auto", TestContext.Current.CancellationToken));
        Assert.Empty(ocr.Calls);
    }

    [Fact]
    public async Task A_second_photo_from_the_same_user_is_turned_away_while_the_first_is_read()
    {
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var ocr = new FakeOcr(async (_, _, _) => { started.TrySetResult(); await release.Task; return new OcrReading([], 1, 1, "x"); });
        var reader = Reader(ocr);

        var first = reader.ReadAsync("alice", Photo(), "auto", TestContext.Current.CancellationToken);
        await started.Task;

        var ex = await Assert.ThrowsAsync<PhotoException>(() => reader.ReadAsync("alice", Photo(), "auto", TestContext.Current.CancellationToken));
        Assert.Equal(PhotoProblem.Busy, ex.Problem);

        // alice can go again once the first is done
        release.SetResult();
        await first;
        await reader.ReadAsync("alice", Photo(), "auto", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_server_wide_limit_applies_across_users()
    {
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var ocr = new FakeOcr(async (_, _, _) => { started.TrySetResult(); await release.Task; return new OcrReading([], 1, 1, "x"); });
        var options = new OcrOptions { MaxConcurrent = 1 };
        var reader = Reader(ocr, options);

        var first = reader.ReadAsync("alice", Photo(), "auto", TestContext.Current.CancellationToken);
        await started.Task;

        var ex = await Assert.ThrowsAsync<PhotoException>(() => reader.ReadAsync("bob", Photo(), "auto", TestContext.Current.CancellationToken));
        Assert.Equal(PhotoProblem.Busy, ex.Problem);
        release.SetResult();
        await first;
    }

    [Fact]
    public async Task A_reader_that_takes_too_long_is_stopped_and_the_slot_is_freed()
    {
        var ocr = new FakeOcr(async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return null!; });
        var reader = Reader(ocr, new OcrOptions { Timeout = TimeSpan.FromMilliseconds(100) });

        var ex = await Assert.ThrowsAsync<PhotoException>(() => reader.ReadAsync("alice", Photo(), "auto", TestContext.Current.CancellationToken));
        Assert.Equal(PhotoProblem.Timeout, ex.Problem);

        var again = Reader(new FakeOcr(), new OcrOptions());   // a fresh gate: the point is the first one released theirs
        await again.ReadAsync("alice", Photo(), "auto", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_slot_is_freed_after_an_error_so_the_user_can_retry()
    {
        var calls = 0;
        var ocr = new FakeOcr((_, _, _) => ++calls == 1 ? throw new PhotoException(PhotoProblem.EngineMissing) : Task.FromResult(new OcrReading([], 1, 1, "x")));
        var reader = Reader(ocr);

        await Assert.ThrowsAsync<PhotoException>(() => reader.ReadAsync("alice", Photo(), "auto", TestContext.Current.CancellationToken));
        await reader.ReadAsync("alice", Photo(), "auto", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Absurdly_many_or_long_lines_from_the_engine_are_trimmed()
    {
        var lines = Enumerable.Range(0, 1000).Select(i => new OcrTextLine(new string('x', 5000), new OcrBox(0, i, 1, 1))).ToList();
        var ocr = new FakeOcr((_, _, _) => Task.FromResult(new OcrReading(lines, 20, 10, "x")));

        var reading = await Reader(ocr).ReadAsync("alice", Photo(), "auto", TestContext.Current.CancellationToken);

        Assert.Equal(PhotoReader.MaxLines, reading.Lines.Count);
        Assert.All(reading.Lines, l => Assert.Equal(PhotoReader.MaxLineLength, l.Text.Length));
    }
}
