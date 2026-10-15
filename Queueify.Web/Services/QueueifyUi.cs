using System.Security.Claims;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.FluentUI.AspNetCore.Components;
using Queueify.Services;
using Queueify.Web.Data;
using Queueify.Web.Storage;

namespace Queueify.Web.Services;

/// <summary>Registration of the shell and the two core pages, kept out of Program.cs so the other tracks merge cleanly.</summary>
public static class QueueifyUi
{
    /// <summary>The first account is the owner; after that, only when the server's configuration opens registration.</summary>
    public static bool RegistrationOpen(bool allowRegistration, bool anyUserExists) => !anyUserExists || allowRegistration;

    /// <summary>
    /// Everything comes from this site, except album covers (any https host, as catalogs choose them). Styles allow
    /// inline because Fluent's components set them; scripts do not. <c>blob:</c> images are the photo preview, made in the
    /// browser from the file the user picked.
    /// </summary>
    public static string ContentSecurityPolicy(string? publicUrl)
    {
        // 'self' covers the circuit's WebSocket in current browsers; older Safari needs the ws origin spelled out.
        var connect = publicUrl is not null && Uri.TryCreate(publicUrl, UriKind.Absolute, out var origin)
            ? $"'self' {(origin.Scheme == Uri.UriSchemeHttps ? "wss" : "ws")}://{origin.Authority}" : "'self'";
        return $"default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' https: data: blob:; " +
            $"font-src 'self' data:; connect-src {connect}; manifest-src 'self'; worker-src 'self'; " +
            "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
    }

    /// <summary>
    /// Cookies carry the Secure flag everywhere except Development, whatever the proxy forwards: behind a TLS-terminating
    /// proxy that is not a trusted hop the app sees plain http and would otherwise issue cookies a plain-http hit could read.
    /// A server that really is kept on plain http (a home LAN) says so with <c>Queueify:AllowInsecureCookies</c>.
    /// </summary>
    public static bool SecureCookies(IConfiguration config, IHostEnvironment env) =>
        !env.IsDevelopment() && !config.GetValue("Queueify:AllowInsecureCookies", false);

    public static WebApplicationBuilder AddQueueifyUi(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddFluentUIComponents();

        if (SecureCookies(builder.Configuration, builder.Environment))
        {
            services.PostConfigureAll<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>(o => o.Cookie.SecurePolicy = CookieSecurePolicy.Always);
            services.AddAntiforgery(o => o.Cookie.SecurePolicy = CookieSecurePolicy.Always);
        }

        services.AddHttpClient(HttpCatalogs.ClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(20);   // ENGINEERING.md: explicit 20 s for HTTP
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Queueify");
        });
        services.AddSingleton<ICatalogs, HttpCatalogs>();
        services.AddScoped<AlbumSearch>();
        services.AddScoped<IUserContext, AuthUserContext>();
        services.AddScoped<UserQueue>();
        services.AddScoped<RegistrationGate>();
        services.AddScoped<Toasts>();
        services.AddScoped<LanguageState>();
        services.AddScoped<CircuitHandler, LocCircuitHandler>();

        // No e-mail goes out from this server, so there is nothing for "confirm your account" to wait for.
        services.Configure<IdentityOptions>(o => o.SignIn.RequireConfirmedAccount = false);
        services.Replace(ServiceDescriptor.Scoped<IdentityErrorDescriber, LocalizedIdentityErrors>());
        return builder;
    }

    /// <summary>
    /// Outside Development (unless <c>Queueify:AllowInsecureCookies</c>) every request counts as https: TLS is meant to end at a
    /// reverse proxy, and the antiforgery and sign-in cookies insist on a secure request. Must run before anything that reads the scheme.
    /// </summary>
    public static WebApplication UseQueueifySecureScheme(this WebApplication app)
    {
        if (SecureCookies(app.Configuration, app.Environment))
            app.Use((context, next) => { context.Request.Scheme = "https"; return next(context); });
        return app;
    }

    public static WebApplication MapQueueifyUi(this WebApplication app)
    {
        // Each request answers in the language of the cookie, or of the browser; circuits do the same
        // through LocCircuitHandler. Loc keeps it per async flow, so two users never swap languages.
        var csp = ContentSecurityPolicy(PublicUrl.Of(app.Configuration));
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers["Content-Security-Policy"] = csp;
                headers["X-Content-Type-Options"] = "nosniff";
                headers["Referrer-Policy"] = "no-referrer";
                return Task.CompletedTask;
            });
            var account = context.User.Identity?.IsAuthenticated == true && context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } id
                ? context.RequestServices.GetRequiredService<PreferencesStore>().Load(id) : null;
            var language = Prefs.Language(account?.Language, context.Request.Cookies[Prefs.LanguageCookie], context.Request.Headers.AcceptLanguage);
            context.Items[nameof(Prefs.Language)] = language;
            context.Items[nameof(Prefs.Theme)] = Prefs.Theme(account?.Theme, context.Request.Cookies[Prefs.ThemeCookie]);
            using (Loc.Override(language)) await next();
        });

        // [FromForm] parameters make the endpoint require the antiforgery token, like every other form post.
        app.MapPost("/prefs", (HttpContext context, [FromForm] string? language, [FromForm] string? theme, [FromForm] string? returnUrl) =>
        {
            var options = new CookieOptions { MaxAge = TimeSpan.FromDays(365), SameSite = SameSiteMode.Lax, IsEssential = true, Secure = context.Request.IsHttps || SecureCookies(context.RequestServices.GetRequiredService<IConfiguration>(), context.RequestServices.GetRequiredService<IHostEnvironment>()), HttpOnly = true };
            if (language is "en" or "es") context.Response.Cookies.Append(Prefs.LanguageCookie, language, options);
            if (theme is "light" or "dark" or "system") context.Response.Cookies.Append(Prefs.ThemeCookie, theme, options);
            // A signed-in account keeps its choice for every browser it uses; Settings shows and changes the same values.
            if (context.User.Identity?.IsAuthenticated == true && context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } id)
            {
                var store = context.RequestServices.GetRequiredService<PreferencesStore>();
                var now = store.Load(id);
                store.Save(id, now with { Language = language is "en" or "es" ? language : now.Language, Theme = theme is "light" or "dark" or "system" ? theme : now.Theme });
            }
            return Results.LocalRedirect(Prefs.LocalReturn(returnUrl));
        }).AllowAnonymous();

        return app;
    }
}

/// <summary>The language the current circuit speaks; set from the page's cookie when the circuit starts.</summary>
public sealed class LanguageState
{
    public string Value { get; set; } = "en";
}

/// <summary>
/// Runs every call into the circuit (clicks, JS results, navigation) with the circuit's language, because
/// <see cref="Loc"/> keeps its override per async flow and a circuit has no request to carry it.
/// </summary>
public sealed class LocCircuitHandler(LanguageState language) : CircuitHandler
{
    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(Func<CircuitInboundActivityContext, Task> next) =>
        async context =>
        {
            using (Loc.Override(language.Value)) await next(context);
        };
}
