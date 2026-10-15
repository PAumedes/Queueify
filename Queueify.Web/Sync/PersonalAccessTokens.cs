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
    public async Task<NewToken> CreateAsync(string userId, string name, CancellationToken ct = default)
    {
        name = name.Trim();
        if (name.Length > MaxNameLength) name = name[..MaxNameLength];
        if (await db.PersonalAccessTokens.CountAsync(t => t.UserId == userId && t.RevokedAt == null, ct) >= MaxPerUser)
            throw new InvalidOperationException("Too many tokens.");

        var stamp = await db.Users.Where(u => u.Id == userId).Select(u => u.SecurityStamp).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("No such user.");

        var token = Prefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        var row = new PersonalAccessToken
        {
            Id = Guid.NewGuid(), UserId = userId, Name = name, TokenHash = Hash(token), CreatedAt = clock.GetUtcNow().UtcDateTime, SecurityStamp = stamp,
        };
        db.PersonalAccessTokens.Add(row);
        await db.SaveChangesAsync(ct);
        return new NewToken(row, token);
    }

    public async Task<IReadOnlyList<PersonalAccessToken>> ListAsync(string userId, CancellationToken ct = default) =>
        await db.PersonalAccessTokens.AsNoTracking().Where(t => t.UserId == userId).OrderBy(t => t.CreatedAt).ToListAsync(ct);

    /// <summary>Revokes one of the user's own tokens; false when it isn't theirs or doesn't exist.</summary>
    public async Task<bool> RevokeAsync(string userId, Guid id, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        // One statement, and an already revoked token keeps its first revocation time.
        var revoked = await db.PersonalAccessTokens.Where(t => t.Id == id && t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        return revoked > 0 || await db.PersonalAccessTokens.AnyAsync(t => t.Id == id && t.UserId == userId, ct);
    }

    /// <summary>The id of the user a live token belongs to, or null for anything else (unknown, revoked, or older than the owner's last security-stamp change).</summary>
    public async Task<string?> ValidateAsync(string token, CancellationToken ct = default)
    {
        if (!LooksLikeToken(token)) return null;
        var hash = Hash(token);
        var row = await (from t in db.PersonalAccessTokens.AsNoTracking()
                         join u in db.Users on t.UserId equals u.Id
                         where t.TokenHash == hash
                         select new { t.Id, t.UserId, t.RevokedAt, t.LastUsedAt, TokenStamp = t.SecurityStamp, UserStamp = u.SecurityStamp })
            .FirstOrDefaultAsync(ct);
        if (row == null || row.RevokedAt != null || row.TokenStamp == null || row.TokenStamp != row.UserStamp) return null;

        var now = clock.GetUtcNow().UtcDateTime;
        if (row.LastUsedAt == null || now - row.LastUsedAt > TimeSpan.FromMinutes(1))
        {
            // Only the timestamp changes, so a revocation made meanwhile is never undone.
            await db.PersonalAccessTokens.Where(t => t.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, now), ct);
        }
        return row.UserId;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
