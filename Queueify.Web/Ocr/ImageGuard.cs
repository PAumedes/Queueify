using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;

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
            var info = Image.Identify(bytes);
            if ((long)info.Width * info.Height > options.MaxPixels) throw new PhotoException(PhotoProblem.TooManyPixels);

            using var image = Image.Load(new DecoderOptions { MaxFrames = 1 }, bytes);
            image.Mutate(x =>
            {
                x.AutoOrient();
                if (Math.Max(image.Width, image.Height) > options.MaxSide)
                    x.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(options.MaxSide, options.MaxSide) });
            });
            image.Metadata.ExifProfile = null;
            image.Metadata.IccProfile = null;
            image.Metadata.XmpProfile = null;

            using var png = new MemoryStream();
            image.Save(png, new PngEncoder());
            return new PreparedImage(png.ToArray(), image.Width, image.Height);
        }
        catch (Exception ex) when (ex is ImageFormatException or InvalidOperationException or NotSupportedException or EndOfStreamException or IOException)
        {
            throw new PhotoException(PhotoProblem.Corrupt);
        }
    }

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
