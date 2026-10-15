namespace Queueify.Web.Services;

/// <summary>Per-browser choices (language, theme) and the redirects around them; pure, so they are tested directly.</summary>
public static class Prefs
{
    public const string LanguageCookie = "qlang";
    public const string ThemeCookie = "qtheme";

    /// <summary>The cookie if it names a translated language; otherwise Spanish when the browser asks for it first.</summary>
    public static string Language(string? cookie, string? acceptLanguage)
    {
        if (cookie is "en" or "es") return cookie;
        var first = (acceptLanguage ?? "").Split(',', 2)[0].Trim();
        return first.StartsWith("es", StringComparison.OrdinalIgnoreCase) ? "es" : "en";
    }

    /// <summary>The account's own choice (Settings, or the shell menu) comes first; "system" or nothing leaves it to the cookie and the browser.</summary>
    public static string Language(string? account, string? cookie, string? acceptLanguage) =>
        account is "en" or "es" ? account : Language(cookie, acceptLanguage);

    public static string Theme(string? account, string? cookie) => account is "light" or "dark" ? account : Theme(cookie);

    public static string Theme(string? cookie) => cookie is "light" or "dark" ? cookie : "system";

    /// <summary>The same-site path to go back to, or "/" for anything that could lead elsewhere.</summary>
    public static string LocalReturn(string? url)
    {
        if (string.IsNullOrEmpty(url) || url[0] != '/' || url.Length > 1 && url[1] is '/' or '\\') return "/";
        // Browsers drop tabs and line breaks inside an address, so "/<tab>/host" would become "//host".
        return url.Any(char.IsControl) ? "/" : url;
    }

    /// <summary>
    /// Where to go after signing in with no page asked for: a phone is almost always opened to add an
    /// album someone just recommended, so it lands on Add. Anywhere asked for wins.
    /// </summary>
    public static string AfterLogin(string? returnUrl, string? userAgent)
    {
        if (!string.IsNullOrEmpty(returnUrl)) return returnUrl;
        return userAgent?.Contains("Mobi", StringComparison.OrdinalIgnoreCase) == true ? "add" : "";
    }
}
