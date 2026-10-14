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

    public static WebApplicationBuilder AddQueueifyUi(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddFluentUIComponents();

        var root = builder.Configuration["Queueify:DataRoot"] ?? Path.Combine(builder.Environment.ContentRootPath, "Data", "users");
        services.TryAddSingleton(new UserStores(root));

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
        return builder;
    }

    public static WebApplication MapQueueifyUi(this WebApplication app)
    {
        using (var scope = app.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.Migrate();

        // Each request answers in the language of the cookie, or of the browser; circuits do the same
        // through LocCircuitHandler. Loc keeps it per async flow, so two users never swap languages.
        app.Use(async (context, next) =>
        {
            var language = Prefs.Language(context.Request.Cookies[Prefs.LanguageCookie], context.Request.Headers.AcceptLanguage);
            context.Items[nameof(Prefs.Language)] = language;
            using (Loc.Override(language)) await next();
        });

        // [FromForm] parameters make the endpoint require the antiforgery token, like every other form post.
        app.MapPost("/prefs", (HttpContext context, [FromForm] string? language, [FromForm] string? theme, [FromForm] string? returnUrl) =>
        {
            var options = new CookieOptions { MaxAge = TimeSpan.FromDays(365), SameSite = SameSiteMode.Lax, IsEssential = true, Secure = context.Request.IsHttps, HttpOnly = true };
            if (language is "en" or "es") context.Response.Cookies.Append(Prefs.LanguageCookie, language, options);
            if (theme is "light" or "dark" or "system") context.Response.Cookies.Append(Prefs.ThemeCookie, theme, options);
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
