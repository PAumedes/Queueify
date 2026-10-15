using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Queueify.Web.Tests.Support;

/// <summary>Signs a request in as the user named by its X-Test-User header, with no cookies or passwords.</summary>
public sealed class HeaderUserAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "HeaderUser";
    public const string Header = "X-Test-User";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(Header, out var user) || user.Count == 0) return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.ToString()), new Claim(ClaimTypes.Name, user.ToString() + "@example.test")], Scheme);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme)));
    }
}

/// <summary>The web app on a scratch data folder, with header-based sign-in and any services swapped.</summary>
public static class ScratchHost
{
    public static WebApplicationFactory<Program> On(WebApplicationFactory<Program> factory, string dataRoot, Action<IServiceCollection>? swap = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Queueify:DataDir", dataRoot);
            // Each host gets its own account database: hosts sharing one file migrate it at the same time and fail.
            Directory.CreateDirectory(dataRoot);
            builder.UseSetting("ConnectionStrings:DefaultConnection", $"DataSource={Path.Combine(dataRoot, "app.db")}");
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication(o => o.DefaultScheme = HeaderUserAuthHandler.Scheme)
                    .AddScheme<AuthenticationSchemeOptions, HeaderUserAuthHandler>(HeaderUserAuthHandler.Scheme, _ => { });
                swap?.Invoke(services);
            });
        });

    public static HttpClient ClientFor(this WebApplicationFactory<Program> host, string? user)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (user is not null) client.DefaultRequestHeaders.Add(HeaderUserAuthHandler.Header, user);
        return client;
    }
}
