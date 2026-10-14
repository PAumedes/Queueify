using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Queueify.Services;
using Queueify.Web.Services;

namespace Queueify.Web.Ocr;

public static class OcrExtensions
{
    /// <summary>Tesseract as the web app's <see cref="IOcr"/>, with the limits from the "Ocr" configuration section.</summary>
    public static IServiceCollection AddQueueifyOcr(this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        var options = config.GetSection("Ocr").Get<OcrOptions>() ?? new OcrOptions();
        services.AddSingleton(options);
        services.AddHttpClient(nameof(TessdataFetcher), c => c.Timeout = TimeSpan.FromMinutes(2));
        services.AddSingleton(sp => new TessdataFetcher(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(TessdataFetcher)),
            Path.Combine(QueueifyStorageExtensions.DataRootOf(config, env), "tessdata"),
            options.TessdataPath));
        services.AddSingleton<IOcr, TesseractOcr>();
        services.AddSingleton<OcrGate>();
        services.AddSingleton<PhotoReader>();
        return services;
    }

    /// <summary><c>POST /api/photo?lang=auto|es|en</c> with the picture as the body.</summary>
    public static IEndpointRouteBuilder MapQueueifyPhoto(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/photo", ReadPhotoAsync).DisableAntiforgery();
        return app;
    }

    private static async Task<IResult> ReadPhotoAsync(HttpContext ctx, PhotoReader reader, OcrOptions options, string? lang, CancellationToken ct)
    {
        if (ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) is not { Length: > 0 } user || ctx.User.Identity?.IsAuthenticated != true)
            return Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);   // with a body, so the status-code page doesn't replace it

        ctx.Response.Headers.CacheControl = "no-store";

        // Only a fetch() carrying a picture type gets here from a browser: a cross-site form can't send one,
        // and without CORS (none is configured) a cross-site fetch is stopped before the request goes out.
        // That, plus SameSite cookies, is the CSRF defence; the real format is still sniffed from the bytes.
        if (ctx.Request.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
            return Problem(PhotoProblem.NotAnImage);
        if ((lang ?? "auto") is not ("auto" or "es" or "en")) return Results.BadRequest(new { error = "language" });
        if (ctx.Request.ContentLength > options.MaxBytes) return Problem(PhotoProblem.TooLarge);

        var limit = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = options.MaxBytes;

        try
        {
            // Kestrel forbids synchronous reads, so the (capped) body is buffered here, not inside the guard.
            using var upload = await BufferAsync(ctx.Request.Body, options.MaxBytes, ct);
            var reading = await reader.ReadAsync(user, upload, lang ?? "auto", ct);
            return Results.Json(new
            {
                width = reading.Width,
                height = reading.Height,
                language = lang ?? "auto",
                languageName = reading.Language,
                lines = reading.Lines.Select(l => new { text = l.Text, x = l.Bounds.X, y = l.Bounds.Y, width = l.Bounds.Width, height = l.Bounds.Height }),
            });
        }
        catch (PhotoException ex)
        {
            return Problem(ex.Problem);
        }
        catch (BadHttpRequestException)
        {
            return Problem(PhotoProblem.TooLarge);
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

    private static IResult Problem(PhotoProblem problem) => Results.Json(
        new { error = char.ToLowerInvariant(problem.ToString()[0]) + problem.ToString()[1..] },
        statusCode: problem switch
        {
            PhotoProblem.TooLarge or PhotoProblem.TooManyPixels => StatusCodes.Status413PayloadTooLarge,
            PhotoProblem.NotAnImage => StatusCodes.Status415UnsupportedMediaType,
            PhotoProblem.Corrupt => StatusCodes.Status422UnprocessableEntity,
            PhotoProblem.Busy => StatusCodes.Status429TooManyRequests,
            PhotoProblem.Timeout => StatusCodes.Status504GatewayTimeout,
            _ => StatusCodes.Status503ServiceUnavailable,
        });
}
