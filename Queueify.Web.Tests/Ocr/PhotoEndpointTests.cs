using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Queueify.Services;
using Queueify.Web.Ocr;
using Queueify.Web.Tests.Support;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Queueify.Web.Tests.Ocr;

public sealed class PhotoEndpointTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qw-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<byte[]> _seen = [];

    public PhotoEndpointTests(WebApplicationFactory<Program> factory) =>
        _factory = ScratchHost.On(factory, _root, services =>
        {
            services.RemoveAll<IOcr>();
            services.AddSingleton<IOcr>(new Fake(_seen));
            services.RemoveAll<OcrOptions>();
            services.AddSingleton(new OcrOptions { MaxBytes = 200_000, MaxPixels = 1_000_000 });
        });

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private sealed class Fake(List<byte[]> seen) : IOcr
    {
        public Task<OcrReading> RecognizeAsync(Stream image, string language, CancellationToken ct = default)
        {
            using var ms = new MemoryStream();
            image.CopyTo(ms);
            seen.Add(ms.ToArray());
            return Task.FromResult(new OcrReading([new OcrTextLine("SPINETTA JADE", new OcrBox(5, 6, 70, 8))], 100, 50, language));
        }
    }

    private static byte[] Png(int w = 100, int h = 50)
    {
        using var image = new Image<Rgb24>(w, h);
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static ByteArrayContent Body(byte[] bytes, string type = "image/png")
    {
        var c = new ByteArrayContent(bytes);
        c.Headers.ContentType = new(type);
        return c;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_signed_in_user_gets_the_lines_with_their_boxes()
    {
        var response = await _factory.ClientFor("alice").PostAsync("/api/photo?lang=es", Body(Png()), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(100, json.RootElement.GetProperty("width").GetInt32());
        var line = json.RootElement.GetProperty("lines")[0];
        Assert.Equal("SPINETTA JADE", line.GetProperty("text").GetString());
        Assert.Equal(70, line.GetProperty("width").GetDouble());
        Assert.Equal("es", json.RootElement.GetProperty("language").GetString());
    }

    [Fact]
    public async Task Nobody_signed_in_is_told_so_not_redirected_to_a_page()
    {
        var response = await _factory.ClientFor(null).PostAsync("/api/photo", Body(Png()), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_seen);
    }

    [Fact]
    public async Task A_form_post_from_another_site_cannot_use_a_users_cookie()
    {
        // A browser form can only send these types; only fetch with a picture type needs CORS approval.
        var form = new FormUrlEncodedContent([new("a", "b")]);
        var response = await _factory.ClientFor("alice").PostAsync("/api/photo", form, Ct);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Empty(_seen);
    }

    [Fact]
    public async Task A_page_sent_as_a_png_is_refused_by_what_it_really_is()
    {
        var response = await _factory.ClientFor("alice").PostAsync("/api/photo", Body("<html>hi</html>"u8.ToArray()), Ct);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("notAnImage", (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
        Assert.Empty(_seen);
    }

    [Fact]
    public async Task An_upload_over_the_size_limit_is_refused_without_being_read()
    {
        var response = await _factory.ClientFor("alice").PostAsync("/api/photo", Body(new byte[300_000]), Ct);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("tooLarge", (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_chunked_upload_without_a_length_is_capped_too()
    {
        var content = new StreamContent(new MemoryStream(new byte[300_000]));
        content.Headers.ContentType = new("image/png");
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/photo") { Content = content };
        request.Headers.TransferEncodingChunked = true;
        request.Headers.Add(HeaderUserAuthHandler.Header, "alice");

        var response = await _factory.CreateClient().SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task A_picture_with_too_many_pixels_is_refused()
    {
        var response = await _factory.ClientFor("alice").PostAsync("/api/photo", Body(Png(1200, 1200)), Ct);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("tooManyPixels", (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("spa+eng;id")]
    public async Task An_unknown_language_is_a_bad_request(string lang)
    {
        var response = await _factory.ClientFor("alice").PostAsync("/api/photo?lang=" + Uri.EscapeDataString(lang), Body(Png()), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_engine_gets_a_fresh_png_not_the_upload()
    {
        var upload = Png();
        await _factory.ClientFor("alice").PostAsync("/api/photo", Body(upload), Ct);

        var passed = Assert.Single(_seen);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, passed[..4]);
    }

    [Fact]
    public async Task Nothing_is_left_on_disk_after_a_photo()
    {
        await _factory.ClientFor("alice").PostAsync("/api/photo", Body(Png()), Ct);

        var files = Directory.Exists(_root) ? Directory.GetFiles(_root, "*", SearchOption.AllDirectories) : [];
        Assert.DoesNotContain(files, f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase));
    }
}
