using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Queueify.Web.Data;

namespace Queueify.Web.Sync;

public sealed record NewToken(PersonalAccessToken Info, string Token);

/// <summary>Creates, lists, revokes and checks the personal access tokens that let a PC sync one user's queue.</summary>
public sealed class PersonalAccessTokens(ApplicationDbContext db, TimeProvider clock)
{
    public const int MaxPerUser = 20;
    public const int MaxNameLength = 100;
    private const string Prefix = "qw_";
    private const int TokenLength = 3 + 43;   // "qw_" + 32 bytes in base64url

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static bool LooksLikeToken(string token) =>
        token.Length == TokenLength && token.StartsWith(Prefix, StringComparison.Ordinal)
        && token.AsSpan(Prefix.Length).IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_") < 0;

    /// <summary>The token text is returned here and nowhere else: only its hash is stored.</summary>
    public async Task<NewToken> CreateAsync(string userId, string name)
    {
        name = name.Trim();
        if (name.Length > MaxNameLength) name = name[..MaxNameLength];
        if (await db.PersonalAccessTokens.CountAsync(t => t.UserId == userId && t.RevokedAt == null) >= MaxPerUser)
            throw new InvalidOperationException("Too many tokens.");

        var token = Prefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        var row = new PersonalAccessToken
        {
            Id = Guid.NewGuid(), UserId = userId, Name = name, TokenHash = Hash(token), CreatedAt = clock.GetUtcNow().UtcDateTime,
        };
        db.PersonalAccessTokens.Add(row);
        await db.SaveChangesAsync();
        return new NewToken(row, token);
    }

    public async Task<IReadOnlyList<PersonalAccessToken>> ListAsync(string userId) =>
        await db.PersonalAccessTokens.AsNoTracking().Where(t => t.UserId == userId).OrderBy(t => t.CreatedAt).ToListAsync();

    /// <summary>Revokes one of the user's own tokens; false when it isn't theirs or doesn't exist.</summary>
    public async Task<bool> RevokeAsync(string userId, Guid id)
    {
        var row = await db.PersonalAccessTokens.FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId);
        if (row == null) return false;
        row.RevokedAt ??= clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>The id of the user a live token belongs to, or null for anything else.</summary>
    public async Task<string?> ValidateAsync(string token)
    {
        if (!LooksLikeToken(token)) return null;
        var hash = Hash(token);
        var row = await db.PersonalAccessTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
        if (row == null || !CryptographicOperations.FixedTimeEquals(row.TokenHash, hash) || row.RevokedAt != null) return null;

        var now = clock.GetUtcNow().UtcDateTime;
        if (row.LastUsedAt == null || now - row.LastUsedAt > TimeSpan.FromMinutes(1))
        {
            // Only the timestamp changes, so a revocation made meanwhile is never undone.
            await db.PersonalAccessTokens.Where(t => t.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, now));
        }
        return row.UserId;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
