using System.Buffers.Binary;
using System.Text;
using SkiaSharp;

namespace Queueify.Web.Tests.Support;

/// <summary>Small real pictures for the photo tests, made with the same library the server decodes with.</summary>
internal static class TestImages
{
    public static byte[] Encode(int w, int h, SKEncodedImageFormat format = SKEncodedImageFormat.Png, SKColor? fill = null)
    {
        using var bitmap = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(fill ?? new SKColor(200, 30, 30));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    public static byte[] Png(int w, int h) => Encode(w, h);

    /// <summary>A JPEG whose top-left quarter is blue and the rest red, to tell which way up it ended.</summary>
    public static byte[] JpegWithBlueTopLeft(int w, int h)
    {
        using var bitmap = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(new SKColor(255, 0, 0));
        using (var canvas = new SKCanvas(bitmap)) canvas.DrawRect(0, 0, w / 2f, h / 2f, new SKPaint { Color = new SKColor(0, 0, 255) });
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Jpeg, 95).ToArray();
    }

    public static MemoryStream PngStream(int w, int h) => new(Png(w, h));

    /// <summary>A JPEG carrying an EXIF block with an orientation and/or a camera make, written by hand because Skia does not write EXIF.</summary>
    public static byte[] JpegWithExif(int w, int h, ushort? orientation = null, string? make = null, byte[]? baseJpeg = null)
    {
        var jpeg = baseJpeg ?? Encode(w, h, SKEncodedImageFormat.Jpeg);
        var entries = new List<(ushort Tag, ushort Type, uint Count, byte[] Value)>();
        var makeBytes = make is null ? [] : Encoding.ASCII.GetBytes(make + "\0");
        if (make is not null) entries.Add((0x010F, 2, (uint)makeBytes.Length, makeBytes));
        if (orientation is { } o) entries.Add((0x0112, 3, 1, [(byte)(o >> 8), (byte)o, 0, 0]));

        var tiff = new List<byte> { 0x4D, 0x4D, 0x00, 0x2A, 0, 0, 0, 8 };
        void U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); tiff.AddRange(b.ToArray()); }
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); tiff.AddRange(b.ToArray()); }
        U16((ushort)entries.Count);
        var spill = (uint)(8 + 2 + 12 * entries.Count + 4);
        var tail = new List<byte>();
        foreach (var (tag, type, count, value) in entries)
        {
            U16(tag); U16(type); U32(count);
            if (value.Length > 4) { U32(spill + (uint)tail.Count); tail.AddRange(value); }
            else tiff.AddRange(value.Concat(new byte[4 - value.Length]));
        }
        U32(0);
        tiff.AddRange(tail);

        var payload = Encoding.ASCII.GetBytes("Exif\0\0").Concat(tiff).ToArray();
        var segment = new List<byte> { 0xFF, 0xE1, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2) };
        segment.AddRange(payload);
        // SOI, then our APP1 first, then whatever Skia wrote (its own JFIF/APP0 segments follow).
        return [.. jpeg[..2], .. segment, .. jpeg[2..]];
    }
}
