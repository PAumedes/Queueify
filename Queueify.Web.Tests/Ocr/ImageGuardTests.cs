using System.Text;
using Queueify.Web.Ocr;
using Queueify.Web.Tests.Support;
using SkiaSharp;

namespace Queueify.Web.Tests.Ocr;

public class ImageGuardTests
{
    private static readonly OcrOptions Defaults = new();

    private static byte[] Picture(int w, int h, string format = "png") => TestImages.Encode(w, h, format switch
    {
        "jpeg" => SKEncodedImageFormat.Jpeg,
        "webp" => SKEncodedImageFormat.Webp,
        _ => SKEncodedImageFormat.Png,
    });

    private static SKImageInfo Info(byte[] bytes)
    {
        using var codec = SKCodec.Create(new MemoryStream(bytes));
        return codec.Info;
    }

    private static PhotoProblem ProblemOf(byte[] bytes, OcrOptions? options = null) =>
        Assert.Throws<PhotoException>(() => ImageGuard.Prepare(new MemoryStream(bytes), options ?? Defaults)).Problem;

    [Theory]
    [InlineData("png")]
    [InlineData("jpeg")]
    [InlineData("webp")]
    public void Real_pictures_are_re_encoded_as_png(string format)
    {
        var prepared = ImageGuard.Prepare(new MemoryStream(Picture(40, 30, format)), Defaults);

        Assert.Equal((40, 30), (prepared.Width, prepared.Height));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, prepared.Png[..4]);
    }

    [Fact]
    public void Metadata_such_as_GPS_does_not_survive_the_re_encode()
    {
        var jpeg = TestImages.JpegWithExif(20, 20, make: "SecretCamera");
        Assert.Contains("SecretCamera", Encoding.Latin1.GetString(jpeg));   // the test input does carry it

        var prepared = ImageGuard.Prepare(new MemoryStream(jpeg), Defaults);

        Assert.DoesNotContain("SecretCamera", Encoding.Latin1.GetString(prepared.Png));
        Assert.DoesNotContain("Exif", Encoding.Latin1.GetString(prepared.Png));
        Assert.Equal((20, 20), (Info(prepared.Png).Width, Info(prepared.Png).Height));
    }

    [Fact]
    public void A_big_photo_is_scaled_down_keeping_its_shape()
    {
        var prepared = ImageGuard.Prepare(new MemoryStream(Picture(4000, 2000)), new OcrOptions { MaxSide = 1000 });
        Assert.Equal((1000, 500), (prepared.Width, prepared.Height));
    }

    [Fact]
    public void A_small_photo_is_not_enlarged()
    {
        var prepared = ImageGuard.Prepare(new MemoryStream(Picture(300, 200)), new OcrOptions { MaxSide = 1000 });
        Assert.Equal((300, 200), (prepared.Width, prepared.Height));
    }

    [Fact]
    public void A_phone_photo_stored_rotated_is_turned_upright()
    {
        var jpeg = TestImages.JpegWithExif(60, 20, orientation: 6);   // rotate 90 clockwise

        var prepared = ImageGuard.Prepare(new MemoryStream(jpeg), Defaults);

        Assert.Equal((20, 60), (prepared.Width, prepared.Height));
    }

    [Theory]
    [InlineData(1, "TL")]
    [InlineData(6, "TR")]   // rotate 90 clockwise: the stored top-left ends at the top-right
    [InlineData(3, "BR")]
    [InlineData(8, "BL")]
    public void The_upright_turn_moves_the_pixels_not_just_the_size(int orientation, string blueCorner)
    {
        var jpeg = TestImages.JpegWithExif(80, 40, (ushort)orientation, baseJpeg: TestImages.JpegWithBlueTopLeft(80, 40));

        var prepared = ImageGuard.Prepare(new MemoryStream(jpeg), Defaults);

        using var bitmap = SKBitmap.Decode(prepared.Png);
        bool Blue(int x, int y) { var c = bitmap.GetPixel(x, y); return c.Blue > 200 && c.Red < 80; }
        var (w, h) = (bitmap.Width, bitmap.Height);
        var corners = new Dictionary<string, bool> { ["TL"] = Blue(2, 2), ["TR"] = Blue(w - 3, 2), ["BL"] = Blue(2, h - 3), ["BR"] = Blue(w - 3, h - 3) };
        Assert.Equal([blueCorner], corners.Where(c => c.Value).Select(c => c.Key));
    }

    [Fact]
    public void Text_pretending_to_be_a_picture_is_refused() =>
        Assert.Equal(PhotoProblem.NotAnImage, ProblemOf(Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>")));

    [Fact]
    public void An_svg_is_refused() =>
        Assert.Equal(PhotoProblem.NotAnImage, ProblemOf(Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'/>")));

    [Fact]
    public void A_gif_or_bmp_is_refused_because_only_photo_formats_are_taken()
    {
        Assert.Equal(PhotoProblem.NotAnImage, ProblemOf(Encoding.ASCII.GetBytes("GIF89a").Concat(new byte[40]).ToArray()));
        Assert.Equal(PhotoProblem.NotAnImage, ProblemOf(Encoding.ASCII.GetBytes("BM").Concat(new byte[60]).ToArray()));
    }

    [Fact]
    public void An_empty_upload_is_refused() => Assert.Equal(PhotoProblem.NotAnImage, ProblemOf([]));

    [Fact]
    public void A_truncated_picture_is_corrupt_not_a_crash()
    {
        var png = Picture(200, 200);
        Assert.Equal(PhotoProblem.Corrupt, ProblemOf(png[..(png.Length / 2)]));
    }

    [Fact]
    public void Too_many_bytes_are_refused_before_anything_is_decoded() =>
        Assert.Equal(PhotoProblem.TooLarge, ProblemOf(Picture(100, 100), new OcrOptions { MaxBytes = 50 }));

    [Fact]
    public void A_picture_with_too_many_pixels_is_refused_from_its_header_alone()
    {
        // 100x100 is small in bytes but over this pixel budget: the guard reads the size without decoding.
        Assert.Equal(PhotoProblem.TooManyPixels, ProblemOf(Picture(100, 100), new OcrOptions { MaxPixels = 5_000 }));
    }

    [Fact]
    public void The_input_stream_is_left_to_the_caller()
    {
        var input = new MemoryStream(Picture(10, 10));
        ImageGuard.Prepare(input, Defaults);
        Assert.True(input.CanRead);
    }
}
