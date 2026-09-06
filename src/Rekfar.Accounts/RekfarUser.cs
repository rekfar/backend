using Microsoft.AspNetCore.Identity;

namespace Rekfar.Accounts;

/// <summary>
/// The credential row: ASP.NET Core Identity's user, mapped onto <c>auth.[User]</c>.
/// </summary>
/// <remarks>
/// Identity remains the user store and the session issuer (ADR-0010 T9), but there is no
/// password anywhere in the product (ADR-0017). What identifies a user here is an email
/// address they have proved they can read, and a security stamp — which is both what the
/// sign-in code is derived from and the only lever a user has over a lost device.
///
/// The two timestamps are not Identity's; they are this schema's convention, and the
/// application maintains them rather than a trigger (database/docs/conventions.md).
/// </remarks>
public sealed class RekfarUser : IdentityUser<Guid>
{
    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
