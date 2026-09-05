namespace Rekfar.Accounts;

/// <summary>
/// The user's profile — the domain identity the API exposes (FR-ACC-3), mapped onto
/// <c>app.[User]</c>.
/// </summary>
/// <remarks>
/// <see cref="Id"/> is not generated: it is the same value as the Identity row's, so a user
/// has one identifier across authentication and domain data and the two cannot drift. The
/// profile row is what makes an account real — a <see cref="RekfarUser"/> without one has
/// only ever been sent a code, and has never proved it could read it.
/// </remarks>
public sealed class UserProfile
{
    public Guid Id { get; set; }

    /// <summary>
    /// Shown wherever the user appears. Seeded from the email address's local part on first
    /// sign-in, because <c>app.[User].DisplayName</c> is NOT NULL and there is nothing else
    /// to seed it from — the user renames themselves from the profile page.
    /// </summary>
    public required string DisplayName { get; set; }

    /// <summary>
    /// BCP-47 tag. <c>nb-NO</c> is the first locale (ADR-0003) and the column's default.
    /// </summary>
    public required string Locale { get; set; }

    /// <summary>
    /// Mapped because the column is NOT NULL and this module is what inserts the row, not
    /// because anything here reads it. Default privacy is FR-ACC-4 and arrives with trips;
    /// until then every profile takes the column's own default, <c>private</c>.
    /// </summary>
    public required string DefaultPrivacy { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
