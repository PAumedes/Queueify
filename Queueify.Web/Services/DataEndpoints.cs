using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Queueify.Web.Storage;

namespace Queueify.Web.Services;

public static class DataEndpoints
{
    /// <summary>Settings, preferences, key tests, and export and import of the queue.</summary>
    public static IServiceCollection AddQueueifyData(this IServiceCollection services)
    {
        services.AddHttpClient("catalogs", c => c.Timeout = TimeSpan.FromSeconds(20));
        services.AddSingleton<DataTransfer>();
        services.AddSingleton<PreferencesStore>();
        services.AddSingleton(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new SettingsService(sp.GetRequiredService<UserStores>(), () => factory.CreateClient("catalogs"));
        });
        return services;
    }

    public static IEndpointRouteBuilder MapQueueifyData(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/export.json", (HttpContext ctx, DataTransfer data) =>
            Download(ctx, "queueify.json", "application/json", Encoding.UTF8.GetBytes, data.ExportJson));
        app.MapGet("/api/export.csv", (HttpContext ctx, DataTransfer data) =>
            Download(ctx, "queueify.csv", "text/csv; charset=utf-8", s => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(s)], data.ExportCsv));
        app.MapPost("/api/import", ImportAsync).DisableAntiforgery();
        return app;
    }

    private static string? UserOf(HttpContext ctx) =>
        ctx.User.Identity?.IsAuthenticated == true && ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } id ? id : null;

    // Responses carry a body even for errors: otherwise the status-code page would replace them.
    private static IResult Error(string code, int status) => Results.Json(new { error = code }, statusCode: status);

    private static IResult Download(HttpContext ctx, string fileName, string type, Func<string, byte[]> encode, Func<string, string> make)
    {
        if (UserOf(ctx) is not { } user) return Error("unauthorized", StatusCodes.Status401Unauthorized);
        ctx.Response.Headers.CacheControl = "no-store";
        return Results.File(encode(make(user)), type, fileName);
    }

    private static async Task<IResult> ImportAsync(HttpContext ctx, DataTransfer data, CancellationToken ct)
    {
        if (UserOf(ctx) is not { } user) return Error("unauthorized", StatusCodes.Status401Unauthorized);
        ctx.Response.Headers.CacheControl = "no-store";

        // Neither type can be sent by a cross-site form, and a cross-site fetch with one needs CORS approval
        // that this app never gives: that, with SameSite cookies, is the CSRF defence.
        var type = ctx.Request.ContentType?.Split(';')[0].Trim();
        if (!string.Equals(type, "application/octet-stream", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(type, "application/json", StringComparison.OrdinalIgnoreCase))
            return Error("invalid", StatusCodes.Status415UnsupportedMediaType);
        if (ctx.Request.ContentLength > DataTransfer.MaxBytes) return Error("tooLarge", StatusCodes.Status413PayloadTooLarge);

        var limit = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = DataTransfer.MaxBytes;

        try
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int n;
            while ((n = await ctx.Request.Body.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + n > DataTransfer.MaxBytes) return Error("tooLarge", StatusCodes.Status413PayloadTooLarge);
                buffer.Write(chunk, 0, n);
            }
            var report = await Task.Run(() => data.Import(user, buffer.ToArray()), ct);
            return Results.Json(new { added = report.Added, alreadyHere = report.AlreadyHere, skipped = report.Skipped });
        }
        catch (ImportException ex)
        {
            return ex.Problem == ImportProblem.TooLarge
                ? Error("tooLarge", StatusCodes.Status413PayloadTooLarge)
                : Error("invalid", StatusCodes.Status422UnprocessableEntity);
        }
        catch (BadHttpRequestException)
        {
            return Error("tooLarge", StatusCodes.Status413PayloadTooLarge);
        }
    }
}
