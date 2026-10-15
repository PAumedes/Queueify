using Bunit;
using Microsoft.JSInterop;
using Queueify.Services;
using Queueify.Web.Components.Shared;
using Queueify.Web.Services;

namespace Queueify.Web.Tests.Pages;

public sealed class SettingsDataTests : BunitContext
{
    private readonly BunitJSModuleInterop _module;

    public SettingsDataTests()
    {
        _module = JSInterop.SetupModule("./js/queueify-upload.js");
        _module.SetupModule("watch", _ => true).SetupVoid("dispose");
        _module.SetupVoid("release", _ => true).SetVoidResult();
    }

    private (IRenderedComponent<SettingsData> Cut, DotNetObjectReference<SettingsData> Page) Open()
    {
        var cut = Render<SettingsData>();
        var watch = Assert.Single(_module.Invocations["watch"]);
        return (cut, (DotNetObjectReference<SettingsData>)watch.Arguments[2]!);
    }

    private async Task ImportAs(IRenderedComponent<SettingsData> cut, DotNetObjectReference<SettingsData> page, UploadResult answer, long size = 100)
    {
        _module.Setup<UploadResult>("send", _ => true).SetResult(answer);
        await cut.InvokeAsync(() => page.Value.OnFileChosen("1", "queueify.db", size, null));
        cut.Find("button.import").Click();
    }

    [Fact]
    public void Offers_json_and_csv_downloads_and_an_import_that_waits_for_a_file()
    {
        var (cut, _) = Open();

        Assert.Equal("/api/export.json", cut.Find("a.json").GetAttribute("href"));
        Assert.Equal("/api/export.csv", cut.Find("a.csv").GetAttribute("href"));
        Assert.NotNull(cut.Find("button.import").GetAttribute("disabled"));
    }

    [Fact]
    public async Task Choosing_a_file_shows_its_name_and_sends_nothing_until_import_is_pressed()
    {
        var (cut, page) = Open();
        await cut.InvokeAsync(() => page.Value.OnFileChosen("1", "queueify.db", 100, null));

        Assert.Contains("queueify.db", cut.Find(".name").TextContent);
        Assert.Null(cut.Find("button.import").GetAttribute("disabled"));
        Assert.Empty(_module.Invocations["send"]);
    }

    [Fact]
    public async Task Import_sends_the_file_as_binary_to_the_import_endpoint_and_reports_the_counts()
    {
        var (cut, page) = Open();
        await ImportAs(cut, page, new UploadResult(200, """{"added":3,"alreadyHere":2,"skipped":1}"""));

        var call = Assert.Single(_module.Invocations["send"]);
        Assert.Equal(["1", "/api/import", "application/octet-stream"], call.Arguments.Select(a => a?.ToString()));
        cut.WaitForAssertion(() => Assert.Equal("✓ " + Loc.F("webData.import.done", 3, 2, 1), cut.Find("[data-status=import]").TextContent));
    }

    [Theory]
    [InlineData(422, """{"error":"invalid"}""", "webData.import.invalid")]
    [InlineData(500, "<html>boom</html>", "webData.import.failed")]
    [InlineData(0, "", "webData.import.failed")]
    public async Task A_refused_file_says_what_to_do_next(int status, string body, string key)
    {
        var (cut, page) = Open();
        await ImportAs(cut, page, new UploadResult(status, body));

        cut.WaitForAssertion(() => Assert.Contains(Loc.T(key), cut.Find("[data-status=import]").TextContent));
        Assert.Contains("error", cut.Find("[data-status=import]").ClassList);
    }

    [Fact]
    public async Task A_file_over_the_limit_is_flagged_at_once()
    {
        var (cut, page) = Open();
        await cut.InvokeAsync(() => page.Value.OnFileChosen("1", "huge.db", 500L * 1024 * 1024, null));

        Assert.Contains(Loc.F("webData.import.too_big", 25), cut.Find("[data-status=import]").TextContent);
    }
}
