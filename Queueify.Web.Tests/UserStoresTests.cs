using Queueify.Models;
using Queueify.Web.Storage;

namespace Queueify.Web.Tests;

public sealed class UserStoresTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qw-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    [Fact]
    public void Each_user_gets_their_own_queue()
    {
        var stores = new UserStores(_root);
        stores.QueueOf("alice").Insert(new BacklogItem { Artist = "A", Album = "One" });

        Assert.Single(stores.QueueOf("alice").LoadAll());
        Assert.Empty(stores.QueueOf("bob").LoadAll());
    }

    [Fact]
    public void Settings_are_kept_per_user()
    {
        var stores = new UserStores(_root);
        stores.SaveSettings("alice", new AppSettings { CountryCode = "ES" });

        Assert.Equal("ES", stores.LoadSettings("alice").CountryCode);
        Assert.Equal("AR", stores.LoadSettings("bob").CountryCode);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData("a\\b")]
    [InlineData(".")]
    [InlineData("keys")]
    [InlineData("a b")]
    public void A_user_id_can_never_leave_the_data_folder(string id) =>
        Assert.Throws<ArgumentException>(() => new UserStores(_root).QueueOf(id));
}
