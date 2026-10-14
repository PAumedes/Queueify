using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Queueify.Services;
using Queueify.Web.Components.Pages;
using Queueify.Web.Ocr;
using Queueify.Web.Services;
using Queueify.Web.Storage;

namespace Queueify.Web.Tests.Pages;

public sealed class PhotoPageTests : BunitContext
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qb-" + Guid.NewGuid().ToString("N"));
    private readonly BunitJSModuleInterop _module;
    private DotNetObjectReference<Photo>? _page;

    public PhotoPageTests()
    {
        var stores = new UserStores(_root);
        Services.AddSingleton(stores);
        Services.AddSingleton(new PreferencesStore(stores));
        Services.AddSingleton(new OcrOptions { MaxBytes = 5 * 1024 * 1024 });
        var auth = AddAuthorization();
        auth.SetAuthorized("alice@example.test");
        auth.SetClaims(new Claim(ClaimTypes.NameIdentifier, "alice"));

        _module = JSInterop.SetupModule("./js/queueify-upload.js");
        var watcher = _module.SetupModule("watch", _ => true);
        watcher.SetupVoid("dispose");
        _module.SetupVoid("release", _ => true).SetVoidResult();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private IRenderedComponent<Photo> Open()
    {
        var cut = Render<Photo>();
        var watch = Assert.Single(_module.Invocations["watch"]);
        _page = (DotNetObjectReference<Photo>)watch.Arguments[2]!;
        return cut;
    }

    private async Task Choose(IRenderedComponent<Photo> cut, string reply, int status = 200, long size = 1000)
    {
        _module.Setup<UploadResult>("send", _ => true).SetResult(new UploadResult(status, reply));
        await cut.InvokeAsync(() => _page!.Value.OnFileChosen("1", "page.jpg", size, "blob:preview"));
    }

    private const string Page = """
        {"width":1000,"height":500,"lines":[
          {"text":"CHARLY GARCÍA","x":100,"y":50,"width":400,"height":50},
          {"text":"Clics modernos (Interdisc, 1983)","x":100,"y":150,"width":600,"height":40}]}
        """;

    [Fact]
    public void Offers_a_picker_a_camera_and_a_language_and_says_nothing_is_kept()
    {
        var cut = Open();

        Assert.Equal("image/*", cut.Find("#photo-choose").GetAttribute("accept"));
        Assert.Equal("environment", cut.Find("#photo-take").GetAttribute("capture"));
        Assert.Equal(["auto", "es", "en"], cut.FindAll("#photo-lang option").Select(o => o.GetAttribute("value")!));
        Assert.Contains(Loc.T("webPhoto.privacy"), cut.Markup);
        Assert.Equal("auto", cut.Find("#photo-lang").GetAttribute("value"));
    }

    [Fact]
    public void The_language_saved_in_settings_is_the_one_shown()
    {
        new PreferencesStore(new UserStores(_root)).Save("alice", Preferences.Default with { OcrLanguage = "es" });
        Assert.Equal("es", Open().Find("#photo-lang").GetAttribute("value"));
    }

    [Fact]
    public void Choosing_another_language_remembers_it()
    {
        var cut = Open();
        cut.Find("#photo-lang").Change("en");
        Assert.Equal("en", new PreferencesStore(new UserStores(_root)).Load("alice").OcrLanguage);
    }

    [Fact]
    public async Task A_read_photo_shows_its_lines_with_boxes_over_the_picture()
    {
        var cut = Open();
        await Choose(cut, Page);

        Assert.Equal("blob:preview", cut.Find(".sheet img").GetAttribute("src"));
        Assert.Equal(2, cut.FindAll(".box").Count);
        Assert.Contains("left:10%;top:10%;width:40%;height:10%", cut.FindAll(".box")[0].GetAttribute("style"));
        Assert.Equal(["CHARLY GARCÍA", "Clics modernos (Interdisc, 1983)"], cut.FindAll(".line").Select(l => l.TextContent.Trim()));
        var request = _module.Invocations["send"].Single();
        Assert.Equal("/api/photo?lang=auto", request.Arguments[1]);
    }

    [Fact]
    public async Task Tapping_the_artist_then_the_album_builds_a_link_to_the_add_page_search()
    {
        var cut = Open();
        await Choose(cut, Page);

        cut.FindAll(".line")[0].Click();
        cut.FindAll(".line")[1].Click();

        var go = cut.Find("a.go");
        Assert.Equal("/add?q=" + Uri.EscapeDataString("Charly García Clics modernos 1983"), go.GetAttribute("href"));
        Assert.Equal("true", cut.FindAll(".line")[0].GetAttribute("aria-pressed"));
    }

    [Fact]
    public async Task Tapping_a_picked_line_again_unpicks_it_and_clearing_removes_the_link()
    {
        var cut = Open();
        await Choose(cut, Page);

        cut.FindAll(".line")[0].Click();
        cut.FindAll(".line")[0].Click();
        Assert.Empty(cut.FindAll("a.go"));

        cut.FindAll(".line")[1].Click();
        cut.Find(".plain").Click();
        Assert.Empty(cut.FindAll("a.go"));
    }

    [Fact]
    public async Task The_box_on_the_photo_picks_the_same_line_as_the_list()
    {
        var cut = Open();
        await Choose(cut, Page);

        cut.FindAll(".box")[0].Click();

        Assert.Equal("true", cut.FindAll(".line")[0].GetAttribute("aria-pressed"));
    }

    [Fact]
    public async Task A_line_with_markup_in_it_is_shown_as_text_and_encoded_in_the_link()
    {
        var cut = Open();
        await Choose(cut, """{"width":10,"height":10,"lines":[{"text":"<img src=x onerror=alert(1)> & \"q\"","x":0,"y":0,"width":5,"height":5}]}""");

        Assert.Empty(cut.FindAll("img[onerror]"));
        cut.Find(".line").Click();
        Assert.DoesNotContain("<img src=x", cut.Find("a.go").GetAttribute("href"));
    }

    [Fact]
    public async Task A_page_without_text_says_so()
    {
        var cut = Open();
        await Choose(cut, """{"width":10,"height":10,"lines":[]}""");

        Assert.Contains(Loc.T("webPhoto.no_text"), cut.Find("[role=status]").TextContent);
        Assert.Empty(cut.FindAll(".sheet"));
    }

    [Theory]
    [InlineData(413, "tooLarge", "webPhoto.err.tooLarge")]
    [InlineData(415, "notAnImage", "webPhoto.err.notAnImage")]
    [InlineData(422, "corrupt", "webPhoto.err.corrupt")]
    [InlineData(429, "busy", "webPhoto.err.busy")]
    [InlineData(504, "timeout", "webPhoto.err.timeout")]
    [InlineData(503, "engineMissing", "webPhoto.err.engineMissing")]
    [InlineData(401, "unauthorized", "webPhoto.err.unauthorized")]
    public async Task Every_server_answer_has_a_message_the_user_can_act_on(int status, string code, string key)
    {
        var cut = Open();
        await Choose(cut, $$"""{"error":"{{code}}"}""", status);

        var expected = key.EndsWith("tooLarge") ? Loc.F(key, 5) : Loc.T(key);
        Assert.Equal(expected, cut.Find(".error").TextContent.Trim());
    }

    [Fact]
    public async Task A_dropped_connection_is_a_network_message_not_a_crash()
    {
        var cut = Open();
        await Choose(cut, "", status: 0);
        Assert.Equal(Loc.T("webPhoto.err.network"), cut.Find(".error").TextContent.Trim());
    }

    [Fact]
    public async Task An_answer_that_is_not_json_is_still_just_a_message()
    {
        var cut = Open();
        await Choose(cut, "<html>502 Bad Gateway</html>", status: 502);
        Assert.NotEmpty(cut.FindAll(".error"));
    }

    [Fact]
    public async Task A_file_over_the_limit_is_not_even_sent()
    {
        var cut = Open();
        await Choose(cut, Page, size: 50L * 1024 * 1024);

        Assert.Empty(_module.Invocations["send"]);
        Assert.Equal(Loc.F("webPhoto.err.tooLarge", 5), cut.Find(".error").TextContent.Trim());
    }

    [Fact]
    public async Task A_second_photo_dropped_while_one_is_being_read_is_let_go_and_cannot_mix_with_the_first()
    {
        var cut = Open();
        var send = _module.Setup<UploadResult>("send", _ => true);
        var first = cut.InvokeAsync(() => _page!.Value.OnFileChosen("1", "a.jpg", 10, "blob:a"));
        cut.WaitForAssertion(() => Assert.Contains(Loc.T("webPhoto.reading"), cut.Markup));

        await cut.InvokeAsync(() => _page!.Value.OnFileChosen("2", "b.jpg", 10, "blob:b"));
        send.SetResult(new UploadResult(200, Page));
        await first;

        Assert.Single(_module.Invocations["send"]);
        Assert.Equal("blob:a", cut.Find(".sheet img").GetAttribute("src"));
        Assert.Contains(_module.Invocations["release"], i => i.Arguments[0]!.ToString() == "2");
    }

    [Fact]
    public async Task A_line_with_no_text_left_after_cleaning_is_no_search_so_no_link_is_offered()
    {
        var cut = Open();
        await Choose(cut, """{"width":10,"height":10,"lines":[{"text":"•","x":0,"y":0,"width":5,"height":5}]}""");

        cut.Find(".line").Click();

        Assert.Empty(cut.FindAll("a.go"));
    }

    [Fact]
    public async Task The_number_of_lines_found_is_announced_in_the_live_region()
    {
        var cut = Open();
        await Choose(cut, Page);

        Assert.Contains(Loc.F("webPhoto.found", 2), cut.Find("[role=status]").TextContent);
    }

    [Fact]
    public async Task An_unknown_failure_says_to_try_again_not_to_call_the_administrator()
    {
        var cut = Open();
        await Choose(cut, "<html>oops</html>", status: 500);
        Assert.Equal(Loc.T("webPhoto.err.unknown"), cut.Find(".error").TextContent.Trim());
    }

    [Fact]
    public async Task The_pickers_are_disabled_while_a_photo_is_being_read()
    {
        var cut = Open();
        var send = _module.Setup<UploadResult>("send", _ => true);   // no answer yet: the read is "in flight"
        var running = cut.InvokeAsync(() => _page!.Value.OnFileChosen("1", "p.jpg", 10, "blob:x"));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#photo-choose").GetAttribute("disabled")));
        Assert.Contains(Loc.T("webPhoto.reading"), cut.Find("[role=status]").TextContent);

        send.SetResult(new UploadResult(200, Page));
        await running;
        Assert.Null(cut.Find("#photo-choose").GetAttribute("disabled"));
    }
}
