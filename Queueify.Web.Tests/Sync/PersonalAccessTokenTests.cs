using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Queueify.Web.Data;
using Queueify.Web.Sync;

namespace Queueify.Web.Tests.Sync;

public sealed class PersonalAccessTokenTests(SyncFactory app) : IClassFixture<SyncFactory>
{
    private async Task<T> WithTokens<T>(Func<PersonalAccessTokens, ApplicationDbContext, Task<T>> body)
    {
        using var scope = app.Services.CreateScope();
        return await body(scope.ServiceProvider.GetRequiredService<PersonalAccessTokens>(), scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    [Fact]
    public async Task A_token_is_qw_and_32_random_bytes_in_base64url()
    {
        var (userId, token) = await app.NewUserAsync("format");
        Assert.Matches("^qw_[A-Za-z0-9_-]{43}$", token);
        Assert.Equal(32, Convert.FromBase64String(token[3..].Replace('-', '+').Replace('_', '/') + "=").Length);
        Assert.NotEqual(token, (await app.NewUserAsync("format2")).Token);
        Assert.NotEmpty(userId);
    }

    [Fact]
    public async Task Only_the_hash_is_stored_and_the_token_is_never_listed()
    {
        var (userId, token) = await app.NewUserAsync("hash");
        await WithTokens(async (tokens, db) =>
        {
            var row = (await tokens.ListAsync(userId)).Single();
            Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(token)), row.TokenHash);
            Assert.Equal("test PC", row.Name);
            Assert.Null(row.LastUsedAt);
            Assert.Null(row.RevokedAt);
            return 0;
        });
        // nothing on disk holds the token itself
        foreach (var file in Directory.EnumerateFiles(app.Dir, "*", SearchOption.AllDirectories))
        {
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            Assert.DoesNotContain(token, Encoding.Latin1.GetString(copy.ToArray()));
        }
    }

    [Fact]
    public async Task Using_a_token_records_when()
    {
        var (userId, token) = await app.NewUserAsync("last-used");
        await app.SyncAsync(token, new Queueify.Services.SyncRequest(null, [], null));
        Assert.NotNull(await WithTokens(async (tokens, _) => (await tokens.ListAsync(userId)).Single().LastUsedAt));
    }

    [Fact]
    public async Task Tokens_are_revoked_only_by_their_owner()
    {
        var (aliceId, aliceToken) = await app.NewUserAsync("own-a");
        var (bobId, _) = await app.NewUserAsync("own-b");
        await WithTokens(async (tokens, _) =>
        {
            var id = (await tokens.ListAsync(aliceId)).Single().Id;
            Assert.False(await tokens.RevokeAsync(bobId, id));
            Assert.Equal(aliceId, await tokens.ValidateAsync(aliceToken));
            Assert.True(await tokens.RevokeAsync(aliceId, id));
            Assert.Null(await tokens.ValidateAsync(aliceToken));
            Assert.NotNull((await tokens.ListAsync(aliceId)).Single().RevokedAt);
            return 0;
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("qw_")]
    [InlineData("qw_short")]
    [InlineData("xx_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Things_that_are_not_tokens_never_validate(string text) =>
        Assert.Null(await WithTokens((tokens, _) => tokens.ValidateAsync(text)));

    [Fact]
    public async Task A_user_can_have_only_so_many_tokens_and_names_are_trimmed_and_limited()
    {
        var (userId, _) = await app.NewUserAsync("limit");
        await WithTokens(async (tokens, _) =>
        {
            var created = await tokens.CreateAsync(userId, "  " + new string('n', 300) + "  ");
            Assert.Equal(100, created.Info.Name.Length);
            for (int i = 0; i < PersonalAccessTokens.MaxPerUser - 2; i++) await tokens.CreateAsync(userId, "t" + i);
            await Assert.ThrowsAsync<InvalidOperationException>(() => tokens.CreateAsync(userId, "one too many"));
            return 0;
        });
    }

    [Fact]
    public async Task Deleting_a_user_removes_their_tokens()
    {
        var (userId, token) = await app.NewUserAsync("gone");
        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();
        await users.DeleteAsync((await users.FindByIdAsync(userId))!);
        Assert.Null(await scope.ServiceProvider.GetRequiredService<PersonalAccessTokens>().ValidateAsync(token));
    }
}
