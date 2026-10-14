using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Queueify.Services;
using Queueify.Web.Data;
using Queueify.Web.Storage;

namespace Queueify.Web.Sync;

public static class SyncExtensions
{
    private const string Policy = "QueueifySync";

    /// <summary>Per-user stores under <c>Queueify:DataDir</c>, persisted Data Protection keys, tokens and the sync engine.</summary>
    public static IServiceCollection AddQueueifySync(this IServiceCollection services)
    {
        // Resolved late, from the final configuration, so tests and hosts can set the folder as they like.
        services.AddSingleton(sp => new DataLocation(Path.GetFullPath(sp.GetRequiredService<IConfiguration>()["Queueify:DataDir"] ?? "./data")));

        services.AddDataProtection().SetApplicationName("Queueify");
        services.AddOptions<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>()
            .Configure<DataLocation>((options, data) =>
                options.XmlRepository = new Microsoft.AspNetCore.DataProtection.Repositories.FileSystemXmlRepository(
                    Directory.CreateDirectory(Path.Combine(data.Root, "keys")), NullLoggerFactory.Instance));

        services.AddSingleton(sp => new UserStores(sp.GetRequiredService<DataLocation>().Root,
            sp.GetRequiredService<IDataProtectionProvider>().CreateProtector("Queueify.UserSecrets.v1")));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<SyncEngine>();
        services.AddSingleton<SyncLimits>();
        services.AddScoped<PersonalAccessTokens>();
        services.AddHostedService<IdentityMigrator>();
        services.AddTransient<IStartupFilter, ProxyStartupFilter>();

        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TokenAuthenticationHandler>(TokenAuthenticationHandler.Scheme, null);
        services.AddAuthorizationBuilder().AddPolicy(Policy, p => p.AddAuthenticationSchemes(TokenAuthenticationHandler.Scheme).RequireAuthenticatedUser());
        return services;
    }

    public static IEndpointRouteBuilder MapQueueifySync(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/sync", SyncAsync).RequireAuthorization(Policy).DisableAntiforgery();
        return app;
    }

    /// <summary>The signed-in user's id (the Identity user id), or null when nobody is signed in.</summary>
    public static string? UserId(this ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier);

    /// <summary>Keeps the site's "not found" page out of the API's answers: an error here has no body.</summary>
    internal static void Quiet(HttpContext http)
    {
        if (http.Features.Get<Microsoft.AspNetCore.Diagnostics.IStatusCodePagesFeature>() is { } pages) pages.Enabled = false;
    }

    private static async Task<IResult> SyncAsync(HttpContext http, SyncEngine engine, SyncLimits limits)
    {
        Quiet(http);
        if (http.User.UserId() is not { } userId) return Results.StatusCode(StatusCodes.Status401Unauthorized);
        if (http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } cap)
            cap.MaxRequestBodySize = limits.MaxBodyBytes;
        if (http.Request.ContentLength > limits.MaxBodyBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        // Read through a cap: the header can be missing or wrong (chunked uploads).
        using var body = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await http.Request.Body.ReadAsync(buffer)) > 0)
        {
            body.Write(buffer, 0, read);
            if (body.Length > limits.MaxBodyBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        SyncRequest? request;
        try { request = JsonSerializer.Deserialize(body.GetBuffer().AsSpan(0, (int)body.Length), SyncJson.Default.SyncRequest); }
        catch (JsonException) { return Results.StatusCode(StatusCodes.Status400BadRequest); }
        if (request == null) return Results.StatusCode(StatusCodes.Status400BadRequest);

        var response = await engine.RunAsync(userId, request);
        return response == null
            ? Results.StatusCode(StatusCodes.Status400BadRequest)
            : Results.Text(JsonSerializer.Serialize(response, SyncJson.Default.SyncResponse), "application/json");
    }
}

/// <summary>
/// Trusts <c>X-Forwarded-For/Proto</c> only from the proxies listed in <c>Queueify:Proxy:KnownProxies</c> (addresses) and
/// <c>Queueify:Proxy:KnownNetworks</c> (CIDR), so the rate limit sees real client addresses behind a reverse proxy.
/// With neither set, the headers are not trusted at all.
/// </summary>
internal sealed class ProxyStartupFilter(IConfiguration configuration) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        var section = configuration.GetSection("Queueify:Proxy");
        var proxies = section.GetSection("KnownProxies").Get<string[]>() ?? [];
        var networks = section.GetSection("KnownNetworks").Get<string[]>() ?? [];
        if (proxies.Length + networks.Length > 0)
        {
            var options = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
            {
                ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                    | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
                ForwardLimit = section.GetValue("ForwardLimit", 1),
            };
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var p in proxies) options.KnownProxies.Add(System.Net.IPAddress.Parse(p));
            foreach (var n in networks) options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(n));
            app.UseForwardedHeaders(options);
        }
        next(app);
    };
}

/// <summary>The root folder that holds every user's data and the Data Protection keys.</summary>
public sealed record DataLocation(string Root);

/// <summary>Brings the Identity database up to date at startup, so a new token table exists before the first request.</summary>
internal sealed class IdentityMigrator(IServiceProvider services) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.MigrateAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
