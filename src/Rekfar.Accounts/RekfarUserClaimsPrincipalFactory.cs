using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Rekfar.Accounts;

/// <summary>
/// Builds the signed-in user's principal.
/// </summary>
/// <remarks>
/// Identity's own factory reads the user's claims from <c>AspNetUserClaims</c> on every
/// sign-in and on every security-stamp refresh. This schema has no such table — by decision,
/// not by omission (database/docs/conventions.md) — so the default factory would query a table
/// the model has ignored.
///
/// There are no per-user claims to add: authorisation in Phase 1 is "is this a user", and the
/// answer is the cookie. What the principal must carry is what the rest of Identity reads back
/// out of it — the user id, the name, and the security stamp that revocation is checked
/// against.
/// </remarks>
internal sealed class RekfarUserClaimsPrincipalFactory(
    UserManager<RekfarUser> userManager,
    IOptions<IdentityOptions> options) : UserClaimsPrincipalFactory<RekfarUser>(userManager, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(RekfarUser user)
    {
        var identity = new ClaimsIdentity(
            IdentityConstants.ApplicationScheme,
            Options.ClaimsIdentity.UserNameClaimType,
            Options.ClaimsIdentity.RoleClaimType);

        identity.AddClaim(new Claim(
            Options.ClaimsIdentity.UserIdClaimType,
            await UserManager.GetUserIdAsync(user)));

        identity.AddClaim(new Claim(
            Options.ClaimsIdentity.UserNameClaimType,
            await UserManager.GetUserNameAsync(user) ?? string.Empty));

        var email = await UserManager.GetEmailAsync(user);

        if (!string.IsNullOrEmpty(email))
        {
            identity.AddClaim(new Claim(Options.ClaimsIdentity.EmailClaimType, email));
        }

        // Without this claim there is nothing for the security-stamp validator to compare, and
        // "log out everywhere" would silently stop working.
        identity.AddClaim(new Claim(
            Options.ClaimsIdentity.SecurityStampClaimType,
            await UserManager.GetSecurityStampAsync(user)));

        return identity;
    }
}
