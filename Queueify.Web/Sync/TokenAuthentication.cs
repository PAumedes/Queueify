using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Queueify.Web.Sync;

/// <summary>Bearer personal access tokens, the only way into <c>/api/sync</c>. It never answers with a body.</summary>
public sealed class TokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    PersonalAccessTokens tokens, SyncLimits limits)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "QueueifyToken";
    private const string ThrottledKey = "queueify.throttled";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Counted before the token is looked at, so guessing tokens is throttled like real syncing.
        using var lease = await limits.Limiter.AcquireAsync(Context, 1);
        if (!lease.IsAcquired)
        {
            Context.Items[ThrottledKey] = true;
            return AuthenticateResult.Fail("throttled");
        }

        var header = Request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        if (!header.StartsWith(bearer, StringComparison.Ordinal)) return AuthenticateResult.NoResult();

        var userId = await tokens.ValidateAsync(header[bearer.Length..].Trim(), Context.RequestAborted);
        if (userId == null) return AuthenticateResult.Fail("bad token");

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], Scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        SyncExtensions.Quiet(Context);
        if (Context.Items.ContainsKey(ThrottledKey))
        {
            Response.StatusCode = StatusCodes.Status429TooManyRequests;
            Response.Headers.RetryAfter = "60";   // the limiter's window is a minute
        }
        else
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            Response.Headers.WWWAuthenticate = "Bearer";
        }
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        SyncExtensions.Quiet(Context);
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}

/// <summary>The endpoint's limits, read once from <c>Queueify:Sync</c>.</summary>
public sealed class SyncLimits : IDisposable
{
    public SyncLimits(IConfiguration configuration)
    {
        var section = configuration.GetSection("Queueify:Sync");
        MaxBodyBytes = Math.Max(1, section.GetValue("MaxBodyBytes", 4 * 1024 * 1024));
        var perMinute = Math.Max(1, section.GetValue("RequestsPerMinute", 120));
        Limiter = PartitionedRateLimiter.Create<HttpContext, string>(c =>
            RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
            }));
    }

    public int MaxBodyBytes { get; }
    public PartitionedRateLimiter<HttpContext> Limiter { get; }

    public void Dispose() => Limiter.Dispose();
}
