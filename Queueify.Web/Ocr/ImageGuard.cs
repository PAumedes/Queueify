using SkiaSharp;

namespace Queueify.Web.Ocr;

/// <summary>Why a photo was not read; the page turns each into a message the user can act on.</summary>
public enum PhotoProblem { TooLarge, NotAnImage, TooManyPixels, Corrupt, Busy, Timeout, EngineMissing }

public sealed class PhotoException(PhotoProblem problem) : Exception(problem.ToString())
{
    public PhotoProblem Problem { get; } = problem;
}

/// <summary>A photo checked, turned upright, scaled down and re-encoded as a plain PNG (no metadata).</summary>
public sealed record PreparedImage(byte[] Png, int Width, int Height);

/// <summary>
/// An upload is untrusted: the declared type means nothing, so the bytes are sniffed, the pixel count is
/// read from the header before anything is decoded, and what leaves is a fresh PNG built by us.
/// </summary>
public static class ImageGuard
{
    public static PreparedImage Prepare(Stream input, OcrOptions options)
    {
        var bytes = ReadCapped(input, options.MaxBytes);
        if (!LooksLikeAPhoto(bytes)) throw new PhotoException(PhotoProblem.NotAnImage);

        try
        {
            // The codec reads the header only; nothing is decoded before the pixel budget is checked.
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data) ?? throw new PhotoException(PhotoProblem.Corrupt);
            var size = codec.Info;
            if (size.Width <= 0 || size.Height <= 0) throw new PhotoException(PhotoProblem.Corrupt);
            if ((long)size.Width * size.Height > options.MaxPixels) throw new PhotoException(PhotoProblem.TooManyPixels);

            using var decoded = new SKBitmap(new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            // Only a complete decode counts: a truncated file is corrupt, not a half-grey picture.
            if (codec.GetPixels(decoded.Info, decoded.GetPixels()) != SKCodecResult.Success) throw new PhotoException(PhotoProblem.Corrupt);

            var origin = codec.EncodedOrigin;
            var (width, height) = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom
                ? (size.Height, size.Width) : (size.Width, size.Height);
            var scale = Math.Max(width, height) > options.MaxSide ? (double)options.MaxSide / Math.Max(width, height) : 1;
            var outW = Math.Max(1, (int)Math.Round(width * scale));
            var outH = Math.Max(1, (int)Math.Round(height * scale));

            // A fresh opaque surface: transparency becomes white (like the printed page) and none of the source
            // metadata can be carried over, because only pixels are drawn.
            using var surface = SKSurface.Create(new SKImageInfo(outW, outH, SKColorType.Rgba8888, SKAlphaType.Opaque))
                ?? throw new PhotoException(PhotoProblem.Corrupt);
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.White);
            canvas.Scale((float)outW / width, (float)outH / height);
            canvas.Concat(Upright(size.Width, size.Height, origin));
            using var source = SKImage.FromBitmap(decoded);
            canvas.DrawImage(source, 0, 0, new SKSamplingOptions(SKCubicResampler.Mitchell));

            using var image = surface.Snapshot();
            using var png = image.Encode(SKEncodedImageFormat.Png, 100) ?? throw new PhotoException(PhotoProblem.Corrupt);
            return new PreparedImage(png.ToArray(), outW, outH);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or EndOfStreamException or IOException or OutOfMemoryException)
        {
            throw new PhotoException(PhotoProblem.Corrupt);
        }
    }

    /// <summary>Maps the stored pixels to the way the camera was held (EXIF orientation values 1 to 8).</summary>
    private static SKMatrix Upright(int w, int h, SKEncodedOrigin origin) => origin switch
    {
        SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
        SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
        SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
        SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
        SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
        _ => SKMatrix.Identity,
    };

    private static byte[] ReadCapped(Stream input, long max)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = input.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + n > max) throw new PhotoException(PhotoProblem.TooLarge);
            buffer.Write(chunk, 0, n);
        }
        return buffer.ToArray();
    }

    /// <summary>JPEG, PNG or WebP by their first bytes; everything else (SVG, HTML, GIF, BMP…) is refused.</summary>
    private static bool LooksLikeAPhoto(ReadOnlySpan<byte> b) =>
        b.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF })
        || b.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })
        || (b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b.Slice(8, 4).SequenceEqual("WEBP"u8));
}
