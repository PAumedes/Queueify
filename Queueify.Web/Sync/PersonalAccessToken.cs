using Microsoft.EntityFrameworkCore;
using Queueify.Web.Data;

namespace Queueify.Web.Sync;

/// <summary>A token a PC uses to sync one user's queue. Only its SHA-256 hash is kept; the token itself is shown once.</summary>
public sealed class PersonalAccessToken
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public byte[] TokenHash { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    internal static void Configure(ModelBuilder model) =>
        model.Entity<PersonalAccessToken>(t =>
        {
            t.ToTable("PersonalAccessTokens");
            t.HasKey(x => x.Id);
            t.Property(x => x.Name).HasMaxLength(100);
            t.Property(x => x.TokenHash).HasMaxLength(32);
            t.HasIndex(x => x.TokenHash).IsUnique();
            t.HasIndex(x => x.UserId);
            t.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
}
