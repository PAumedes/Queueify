using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Queueify.Web.Data;

namespace Queueify.Web.Tests;

/// <summary>
/// The hand-kept migration snapshot must describe the model exactly, or every start of the app throws. The model
/// depends on Identity's options (schema version 3 adds passkeys), so these build the context the way the app does,
/// from its own service provider; a bare context would cache a different model for the whole process.
/// </summary>
public sealed class MigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qw-mig-" + Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<Program>? _factory;

    public void Dispose()
    {
        _factory?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private IServiceProvider App() => (_factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        Directory.CreateDirectory(_dir);
        b.UseSetting("ConnectionStrings:DefaultConnection", $"DataSource={Path.Combine(_dir, "app.db")};Pooling=False");
        b.UseSetting("Queueify:DataDir", Path.Combine(_dir, "data"));
    })).Services;

    [Fact]
    public void The_snapshot_matches_the_model_the_app_builds()
    {
        using var scope = App().CreateScope();
        Assert.False(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.HasPendingModelChanges(),
            "Add a migration: the model changed since the last one.");
    }

    [Fact]
    public void The_app_starts_on_a_new_database_and_leaves_nothing_pending()
    {
        using var scope = App().CreateScope();   // starting the host ran the migrations
        Assert.Empty(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.GetPendingMigrations());
    }

    [Fact]
    public async Task Migrating_again_from_several_places_at_once_changes_nothing()
    {
        var services = App();
        var errors = new List<Exception>();
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
        {
            try { using var scope = services.CreateScope(); scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.Migrate(); }
            catch (Exception ex) { lock (errors) errors.Add(ex); }
        })));
        Assert.Empty(errors);
    }
}
