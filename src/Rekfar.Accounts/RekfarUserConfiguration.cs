using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Rekfar.Accounts;

/// <summary>
/// Maps <see cref="RekfarUser"/> onto <c>auth.[User]</c>.
/// </summary>
/// <remarks>
/// Identity's default table and column names are not used; the schema is owned by the
/// database repository (ADR-0013), so this mapping is a stated contract with it.
///
/// It maps <b>the columns this module uses, not the columns Identity has</b> — the same
/// subsetting the catalogue applies to <c>[ref].Peak</c>. What is ignored is ignored for a
/// reason:
///
/// <list type="bullet">
/// <item><c>PasswordHash</c> — no password exists anywhere in Rekfar (ADR-0017), and the
/// column is being removed from the schema (rekfar/database#6). A model that never names it
/// is correct on both sides of that change.</item>
/// <item><c>PhoneNumber</c>, <c>PhoneNumberConfirmed</c> — Identity has them; this schema
/// does not, and no feature asks for them.</item>
/// <item><c>TwoFactorEnabled</c>, and the three lockout columns — the sign-in code is the
/// only factor, and the cap on guessing it lives in <see cref="SignInCodeLedger"/>, which
/// also covers addresses that have no user row at all. Mapping Identity's lockout as well
/// would be a second mechanism for one rule. Those columns stay NOT NULL with defaults, so
/// an insert that never mentions them is fine.</item>
/// </list>
/// </remarks>
internal sealed class RekfarUserConfiguration : IEntityTypeConfiguration<RekfarUser>
{
    public void Configure(EntityTypeBuilder<RekfarUser> builder)
    {
        builder.ToTable("User", "auth");

        builder.HasKey(user => user.Id);

        // uniqueidentifier, per the database's split of ref (bigint) from app/auth (guid).
        // The column defaults to NEWSEQUENTIALID(); EF generates a sequential value client
        // side instead, which is the same intent and keeps the id available before the
        // insert — the profile row in [app] is keyed on it.
        builder.Property(user => user.Id)
            .HasColumnName("Id")
            .ValueGeneratedOnAdd();

        // The user name is the email address. Identity requires one, this product has no
        // separate concept of it, and a second name to keep in step would only be a way for
        // the two to disagree.
        builder.Property(user => user.UserName)
            .HasColumnName("UserName")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(user => user.NormalizedUserName)
            .HasColumnName("NormalizedUserName")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(user => user.Email)
            .HasColumnName("Email")
            .HasMaxLength(256)
            .IsRequired();

        // Upper-cased by Identity's lookup normaliser. UX_auth_User_NormalizedEmail is what
        // actually enforces one account per address (FR-ACC-1).
        builder.Property(user => user.NormalizedEmail)
            .HasColumnName("NormalizedEmail")
            .HasMaxLength(256)
            .IsRequired();

        // Set when a code is first verified: verifying the code *is* the email confirmation
        // (ADR-0017), so there is no separate confirmation link.
        builder.Property(user => user.EmailConfirmed)
            .HasColumnName("EmailConfirmed");

        // Both the seed the sign-in code is derived from and the revocation lever: rotating
        // it invalidates every outstanding code and every session at once.
        builder.Property(user => user.SecurityStamp)
            .HasColumnName("SecurityStamp");

        builder.Property(user => user.ConcurrencyStamp)
            .HasColumnName("ConcurrencyStamp")
            .IsConcurrencyToken();

        builder.Property(user => user.CreatedAt)
            .HasColumnName("CreatedAt")
            .HasColumnType("datetime2(3)");

        builder.Property(user => user.UpdatedAt)
            .HasColumnName("UpdatedAt")
            .HasColumnType("datetime2(3)");

        builder.Ignore(user => user.PasswordHash);
        builder.Ignore(user => user.PhoneNumber);
        builder.Ignore(user => user.PhoneNumberConfirmed);
        builder.Ignore(user => user.TwoFactorEnabled);
        builder.Ignore(user => user.LockoutEnabled);
        builder.Ignore(user => user.LockoutEnd);
        builder.Ignore(user => user.AccessFailedCount);
    }
}
