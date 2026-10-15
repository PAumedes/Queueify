using Queueify.Web.Services;

namespace Queueify.Web.Tests;

/// <summary>Only requests that draw a page need the signed-in user's language and theme; files and framework scripts do not.</summary>
public class PreferencesNeededTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/add")]
    [InlineData("/Account/Login")]
    [InlineData("/Account/Manage/ChangePassword")]
    [InlineData("/not-found")]
    [InlineData("/_blazor/negotiate")]
    [InlineData("/prefs")]
    public void Pages_and_forms_need_them(string path) => Assert.True(QueueifyUi.NeedsPreferences(path));

    [Theory]
    [InlineData("/queueify.css")]
    [InlineData("/queueify.abc123.js")]
    [InlineData("/js/queueify-upload.js")]
    [InlineData("/icons/icon-512.png")]
    [InlineData("/manifest.webmanifest")]
    [InlineData("/sw.js")]
    [InlineData("/favicon.png")]
    [InlineData("/_framework/blazor.web.js")]
    [InlineData("/_content/Microsoft.FluentUI.AspNetCore.Components/x.bundle.scp.css")]
    [InlineData("/Components/Layout/ReconnectModal.razor.js")]
    public void Static_files_do_not(string path) => Assert.False(QueueifyUi.NeedsPreferences(path));
}
