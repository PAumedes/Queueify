using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Queueify.Models;
using Queueify.Services;
using Queueify.Web.Sync;

namespace Queueify.Web.Tests.Sync;

/// <summary>"Delete my data" and the sync engine's memory of a user's file must not fight each other.</summary>
public sealed class DeleteDataTests(SyncFactory app) : IClassFixture<SyncFactory>
{
    private static SyncRequest Empty => new(null, [], null);
    private SyncEngine Engine => app.Services.GetRequiredService<SyncEngine>();

    [Fact]
    public async Task Syncing_still_works_after_the_users_data_was_deleted_and_the_pages_made_a_new_queue()
    {
        var (userId, token) = await app.NewUserAsync("forget");
        Assert.Equal(HttpStatusCode.OK, (await app.SyncAsync(token, Empty)).Status);

        await Engine.DeleteUserDataAsync(userId);
        // The next visit to a page creates the desktop-schema file again, without the server's sync columns.
        app.Stores.QueueOf(userId).Insert(new BacklogItem { Artist = "A", Album = "Fresh" });

        var (status, body) = await app.SyncAsync(token, Empty);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Fresh", Assert.Single(body!.Albums).Album);
    }

    [Fact]
    public async Task Syncing_right_after_the_deletion_starts_a_new_queue_instead_of_failing()
    {
        var (userId, token) = await app.NewUserAsync("forget-2");
        Assert.Equal(HttpStatusCode.OK, (await app.SyncAsync(token, Empty)).Status);

        await Engine.DeleteUserDataAsync(userId);

        Assert.Equal(HttpStatusCode.OK, (await app.SyncAsync(token, Empty)).Status);
    }
}
