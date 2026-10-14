using Queueify.Web.Services;

namespace Queueify.Web.Tests;

/// <summary>Language and theme come from cookies, then from the browser's own language.</summary>
public class PrefsTests
{
    [Theory]
    [InlineData("es", "en-US,en;q=0.9", "es")]       // the cookie is the user's choice
    [InlineData("en", "es-AR,es;q=0.9", "en")]
    [InlineData(null, "es-AR,es;q=0.9,en;q=0.8", "es")]
    [InlineData(null, "es", "es")]
    [InlineData(null, "en-GB,en;q=0.9", "en")]
    [InlineData(null, "fr-FR,fr;q=0.9", "en")]       // not translated: English
    [InlineData(null, null, "en")]
    [InlineData("klingon", "es-AR", "es")]           // a made-up cookie is ignored
    [InlineData("", "", "en")]
    public void Language_ComesFromTheCookieThenTheBrowser(string? cookie, string? accept, string expected) =>
        Assert.Equal(expected, Prefs.Language(cookie, accept));

    [Theory]
    [InlineData("dark", "dark")]
    [InlineData("light", "light")]
    [InlineData("system", "system")]
    [InlineData(null, "system")]
    [InlineData("<script>", "system")]
    public void Theme_IsOneOfThree(string? cookie, string expected) => Assert.Equal(expected, Prefs.Theme(cookie));

    [Theory]
    [InlineData("/add", "/add")]
    [InlineData("/listened?x=1", "/listened?x=1")]
    [InlineData("//evil.example", "/")]
    [InlineData("https://evil.example", "/")]
    [InlineData("\\\\evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("", "/")]
    [InlineData(null, "/")]
    public void LocalReturn_NeverLeavesTheSite(string? url, string expected) => Assert.Equal(expected, Prefs.LocalReturn(url));

    [Theory]
    [InlineData("", "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) Mobile/15E148", "add")]
    [InlineData(null, "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit Mobile Safari", "add")]
    [InlineData("", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120", "")]
    [InlineData("listened", "Mozilla/5.0 (iPhone) Mobile", "listened")]   // somewhere was asked for: go there
    [InlineData(null, null, "")]
    public void AfterLogin_PhonesStartOnAdd(string? returnUrl, string? userAgent, string expected) =>
        Assert.Equal(expected, Prefs.AfterLogin(returnUrl, userAgent));
}
