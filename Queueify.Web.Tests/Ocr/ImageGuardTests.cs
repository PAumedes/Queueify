using System.Text;
using Queueify.Web.Ocr;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace Queueify.Web.Tests.Ocr;

public class ImageGuardTests
{
    private static readonly OcrOptions Defaults = new();

    private static byte[] Picture(int w, int h, string format = "png")
    {
        using var image = new Image<Rgb24>(w, h, new Rgb24(200, 30, 30));
        using var ms = new MemoryStream();
        switch (format)
        {
            case "jpeg": image.SaveAsJpeg(ms); break;
            case "webp": image.SaveAsWebp(ms); break;
            default: image.SaveAsPng(ms); break;
        }
        return ms.ToArray();
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
        using var image = new Image<Rgb24>(20, 20);
        image.Metadata.ExifProfile = new SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifProfile();
        image.Metadata.ExifProfile.SetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.Make, "SecretCamera");
        using var jpeg = new MemoryStream();
        image.SaveAsJpeg(jpeg);
        Assert.Contains("SecretCamera", Encoding.Latin1.GetString(jpeg.ToArray()));   // the test input does carry it

        var prepared = ImageGuard.Prepare(new MemoryStream(jpeg.ToArray()), Defaults);

        Assert.DoesNotContain("SecretCamera", Encoding.Latin1.GetString(prepared.Png));
        Assert.Null(Image.Load(prepared.Png).Metadata.ExifProfile);
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
        using var image = new Image<Rgb24>(60, 20);
        image.Metadata.ExifProfile = new SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifProfile();
        image.Metadata.ExifProfile.SetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.Orientation, (ushort)6);   // rotate 90 clockwise
        using var jpeg = new MemoryStream();
        image.SaveAsJpeg(jpeg);

        var prepared = ImageGuard.Prepare(new MemoryStream(jpeg.ToArray()), Defaults);

        Assert.Equal((20, 60), (prepared.Width, prepared.Height));
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
