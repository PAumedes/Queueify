using Microsoft.AspNetCore.Mvc.Testing;

namespace Queueify.Web.Tests;

/// <summary>Where the account database lives: with the rest of the server's data, never inside the source tree or the build output.</summary>
public sealed class AccountDatabaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qw-acct-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Queueify.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Queueify.slnx not found above the test output.");
    }

    [Fact]
    public async Task Without_a_connection_string_the_account_database_is_created_under_the_data_folder()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("Queueify:DataDir", Path.Combine(_dir, "data")));
        using var client = factory.CreateClient();   // starting the host migrates the database

        Assert.True(File.Exists(Path.Combine(_dir, "data", "identity.db")));
    }

    [Fact]
    public void No_account_database_is_part_of_the_source_or_the_build_output()
    {
        var web = Path.Combine(RepoRoot(), "Queueify.Web");
        Assert.False(File.Exists(Path.Combine(web, "Data", "app.db")));
        Assert.DoesNotContain("app.db", File.ReadAllText(Path.Combine(web, "Queueify.Web.csproj")));
        Assert.DoesNotContain("ConnectionStrings", File.ReadAllText(Path.Combine(web, "appsettings.json")));
        Assert.Contains("*.db", File.ReadAllLines(Path.Combine(RepoRoot(), ".gitignore")).Select(l => l.Trim()));
    }
}
