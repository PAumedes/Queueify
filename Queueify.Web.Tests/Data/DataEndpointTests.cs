using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Queueify.Models;
using Queueify.Web.Services;
using Queueify.Web.Storage;
using Queueify.Web.Tests.Support;

namespace Queueify.Web.Tests.Data;

public sealed class DataEndpointTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qe-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;

    public DataEndpointTests(WebApplicationFactory<Program> factory) => _factory = ScratchHost.On(factory, _root);
    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(_root, true); } catch (IOException) { } }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private UserStores Stores() => new(_root);   // same folders as the app; secrets are not needed here

    private void Seed(string user, string artist, string album, string uid)
    {
        Stores().QueueOf(user).Insert(new BacklogItem { Artist = artist, Album = album, Uid = uid, AddedAt = DateTime.UtcNow });
    }

    private static ByteArrayContent Body(byte[] bytes, string type = "application/octet-stream")
    {
        var c = new ByteArrayContent(bytes);
        c.Headers.ContentType = new(type);
        return c;
    }

    [Theory]
    [InlineData("/api/export.json")]
    [InlineData("/api/export.csv")]
    public async Task Exports_need_a_signed_in_user(string url)
    {
        var response = await _factory.ClientFor(null).GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_json_export_is_a_download_of_only_my_albums_and_never_cached()
    {
        Seed("alice", "Charly García", "Clics modernos", "a1");
        Seed("bob", "Bob's Band", "Bob's Album", "b1");

        var response = await _factory.ClientFor("alice").GetAsync("/api/export.json", Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.EndsWith(".json", response.Content.Headers.ContentDisposition.FileName!.Trim('"'));
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Contains("Clics modernos", text);
        Assert.DoesNotContain("Bob", text);
    }

    [Fact]
    public async Task The_csv_export_opens_in_a_spreadsheet_with_its_accents()
    {
        Seed("alice", "Charly García", "Clics modernos", "a1");

        var response = await _factory.ClientFor("alice").GetAsync("/api/export.csv", Ct);
        var bytes = await response.Content.ReadAsByteArrayAsync(Ct);

        Assert.StartsWith("text/csv", response.Content.Headers.ContentType!.ToString());
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);   // BOM: Excel reads UTF-8 only with it
        Assert.Contains("Charly García", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task Importing_an_export_brings_the_albums_in_once()
    {
        Seed("alice", "A", "One", "a1");
        var json = await (await _factory.ClientFor("alice").GetAsync("/api/export.json", Ct)).Content.ReadAsByteArrayAsync(Ct);

        var first = await _factory.ClientFor("bob").PostAsync("/api/import", Body(json, "application/json"), Ct);
        var second = await _factory.ClientFor("bob").PostAsync("/api/import", Body(json, "application/json"), Ct);

        var report = await first.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(1, report.GetProperty("added").GetInt32());
        Assert.Equal(1, (await second.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("alreadyHere").GetInt32());
        Assert.Single(Stores().QueueOf("bob").LoadAll());
    }

    [Fact]
    public async Task Import_needs_a_signed_in_user()
    {
        var response = await _factory.ClientFor(null).PostAsync("/api/import", Body("{}"u8.ToArray()), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_cross_site_form_cannot_import_into_someones_queue()
    {
        var form = new FormUrlEncodedContent([new("a", "b")]);
        var plain = new StringContent("{}", Encoding.UTF8, "text/plain");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await _factory.ClientFor("alice").PostAsync("/api/import", form, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await _factory.ClientFor("alice").PostAsync("/api/import", plain, Ct)).StatusCode);
    }

    [Fact]
    public async Task A_crafted_database_is_refused_with_a_message_code_and_changes_nothing()
    {
        var path = Path.Combine(_root, "evil.db");
        Directory.CreateDirectory(_root);
        using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            db.Open();
            foreach (var sql in new[] { "CREATE TABLE other (artist, album, added_at)", "CREATE VIEW items AS SELECT artist, album, added_at FROM other" })
            { using var c = db.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
        }
        SqliteConnection.ClearAllPools();

        var response = await _factory.ClientFor("alice").PostAsync("/api/import", Body(await File.ReadAllBytesAsync(path, Ct)), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("invalid", (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
        Assert.Empty(Stores().QueueOf("alice").LoadAll());
    }

    [Fact]
    public async Task An_upload_over_the_limit_is_refused_by_its_length()
    {
        var response = await _factory.ClientFor("alice").PostAsync("/api/import", Body(new byte[DataTransfer.MaxBytes + 10]), Ct);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task A_chunked_upload_over_the_limit_is_stopped_while_reading()
    {
        var content = new StreamContent(new MemoryStream(new byte[DataTransfer.MaxBytes + 10]));
        content.Headers.ContentType = new("application/octet-stream");
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/import") { Content = content };
        request.Headers.TransferEncodingChunked = true;
        request.Headers.Add(HeaderUserAuthHandler.Header, "alice");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await _factory.CreateClient().SendAsync(request, Ct)).StatusCode);
    }

    [Fact]
    public async Task No_response_here_ever_carries_a_saved_secret()
    {
        Seed("alice", "A", "One", "a1");
        var stores = new UserStores(_root);
        stores.SaveSettings("alice", new AppSettings { SpotifyClientId = "SPOTID", SpotifyClientSecret = "SPOTSECRET", TidalClientSecret = "TIDALSECRET" });
        var client = _factory.ClientFor("alice");

        foreach (var url in new[] { "/api/export.json", "/api/export.csv" })
        {
            var text = await (await client.GetAsync(url, Ct)).Content.ReadAsStringAsync(Ct);
            Assert.DoesNotContain("SECRET", text);
        }
    }
}
