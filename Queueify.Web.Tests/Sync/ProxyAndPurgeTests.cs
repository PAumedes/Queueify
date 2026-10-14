using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Queueify.Services;
using Queueify.Web.Storage;
using Queueify.Web.Sync;

namespace Queueify.Web.Tests.Sync;

public sealed class ProxyAndPurgeTests
{
    private sealed class FakeIpFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, n) => { context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.5"); return n(context); });
            next(app);
        };
    }

    private class Behind(string proxies) : SyncFactory(new()
    {
        ["Queueify:Sync:RequestsPerMinute"] = "2",
        ["Queueify:Proxy:KnownProxies:0"] = proxies,
    })
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s => s.Insert(0, ServiceDescriptor.Transient<IStartupFilter, FakeIpFilter>()));
        }
    }

    private sealed class TrustedProxy() : Behind("10.0.0.5");
    private sealed class OtherProxy() : Behind("10.9.9.9");

    private static async Task<HttpStatusCode> Hit(SyncFactory app, string forwardedFor)
    {
        var request = SyncFactory.Post("qw_wrongwrongwrongwrongwrongwrongwrongwrongwro", "{}");
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        using var response = await app.CreateClient().SendAsync(request);
        return response.StatusCode;
    }

    [Fact]
    public async Task Behind_a_trusted_proxy_each_client_address_has_its_own_limit()
    {
        await using var app = new TrustedProxy();
        Assert.Equal(HttpStatusCode.Unauthorized, await Hit(app, "203.0.113.1"));
        Assert.Equal(HttpStatusCode.Unauthorized, await Hit(app, "203.0.113.1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await Hit(app, "203.0.113.1"));
        Assert.Equal(HttpStatusCode.Unauthorized, await Hit(app, "203.0.113.2"));
    }

    [Fact]
    public async Task A_forwarded_header_from_an_untrusted_sender_is_ignored()
    {
        await using var app = new OtherProxy();
        Assert.Equal(HttpStatusCode.Unauthorized, await Hit(app, "203.0.113.1"));
        Assert.Equal(HttpStatusCode.Unauthorized, await Hit(app, "203.0.113.2"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await Hit(app, "203.0.113.3"));   // spoofing doesn't dodge the limit
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static SyncAlbum Album(string uid, DateTime updated, bool deleted) =>
        new(uid, "A", "B", null, null, null, null, null, updated, null, -1, updated, deleted);

    [Fact]
    public async Task A_user_cannot_hold_more_rows_than_the_quota_and_the_refused_request_changes_nothing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qw-quota-" + Guid.NewGuid().ToString("N"));
        try
        {
            var engine = new SyncEngine(new UserStores(dir), TimeProvider.System, maxRowsPerUser: 5);
            var now = DateTime.UtcNow;
            Assert.NotNull(await engine.RunAsync("u", new SyncRequest(null, [.. Enumerable.Range(0, 5).Select(i => Album("a" + i, now, false))], null)));

            await Assert.ThrowsAsync<SyncQuotaException>(() => engine.RunAsync("u", new SyncRequest(null, [Album("one-more", now, false)], null)));
            // existing rows can still be changed, and nothing of the refused request was kept
            var after = await engine.RunAsync("u", new SyncRequest(null, [Album("a0", now.AddMinutes(1), true)], null));
            Assert.DoesNotContain(after!.Albums, a => a.Uid == "one-more");
            Assert.Empty((await engine.RunAsync("u", new SyncRequest(null, [], null)))!.Albums.Where(a => a.Uid == "one-more"));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task The_identity_model_has_no_pending_changes()
    {
        await using var app = new SyncFactory();
        using var scope = app.Services.CreateScope();
        Assert.False(scope.ServiceProvider.GetRequiredService<Queueify.Web.Data.ApplicationDbContext>().Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Deleted_rows_older_than_90_days_are_purged_and_live_or_recent_ones_stay()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qw-purge-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new Clock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var engine = new SyncEngine(new UserStores(dir), clock);
            var t0 = clock.Now.UtcDateTime;
            await engine.RunAsync("u", new SyncRequest(null, [Album("old-del", t0, true), Album("recent-del", t0, true), Album("live", t0, false)], null));

            clock.Now = clock.Now.AddDays(60);
            await engine.RunAsync("u", new SyncRequest(null, [Album("recent-del", clock.Now.UtcDateTime, true)], null));
            clock.Now = clock.Now.AddDays(31);   // 91 days after the first; 31 after the second
            var all = (await engine.RunAsync("u", new SyncRequest(null, [], null)))!.Albums.Select(a => a.Uid).Order().ToList();

            Assert.Equal(["live", "recent-del"], all);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
