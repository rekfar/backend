using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Rekfar.Accounts;

/// <summary>The signed-in user's own profile (FR-ACC-3).</summary>
/// <remarks>
/// Every endpoint here requires authentication (NFR-SEC-3). The catalogue stays anonymously
/// readable; nothing that belongs to a person does.
/// </remarks>
public static class MeEndpoints
{
    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapGet("/me", GetMe)
            .WithName("GetProfile")
            .WithSummary("The signed-in user's profile")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        builder.MapPatch("/me", UpdateMe)
            .WithName("UpdateProfile")
            .WithSummary("Change display name or locale")
            .WithDescription(
                "A field that is absent or null is left unchanged, which is what makes this a "
                + "PATCH. Returns the profile as it now stands.")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        return builder;
    }

    private static async Task<Results<Ok<Profile>, ProblemHttpResult>> GetMe(
        HttpContext httpContext,
        UserManager<RekfarUser> users,
        AccountsDbContext database,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(httpContext, users, out var userId))
        {
            return NotSignedIn();
        }

        // One query across both schemas rather than two round trips. The join is written out
        // because the two rows are related by a shared primary key and nothing else — there is
        // no navigation property to lean on, and stating it here is clearer than one.
        var profile = await (
            from row in database.Profiles.AsNoTracking()
            join user in database.Users on row.Id equals user.Id
            where row.Id == userId
            select new Profile(user.Email!, row.DisplayName, row.Locale))
            .FirstOrDefaultAsync(cancellationToken);

        return profile is null ? MissingProfile() : TypedResults.Ok(profile);
    }

    private static async Task<Results<Ok<Profile>, ProblemHttpResult>> UpdateMe(
        ProfileUpdate update,
        HttpContext httpContext,
        UserManager<RekfarUser> users,
        AccountsDbContext database,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(httpContext, users, out var userId))
        {
            return NotSignedIn();
        }

        var displayName = (string?)null;

        if (update.DisplayName is not null)
        {
            if (!UserProfileEdit.TryParseDisplayName(update.DisplayName, out var parsed, out var error))
            {
                return Invalid(error);
            }

            displayName = parsed;
        }

        var locale = (string?)null;

        if (update.Locale is not null)
        {
            if (!UserProfileEdit.TryParseLocale(update.Locale, out var parsed, out var error))
            {
                return Invalid(error);
            }

            locale = parsed;
        }

        var profile = await database.Profiles
            .FirstOrDefaultAsync(row => row.Id == userId, cancellationToken);

        if (profile is null)
        {
            return MissingProfile();
        }

        // Assigned after both fields have validated, so a bad locale cannot leave a new display
        // name half-applied.
        profile.DisplayName = displayName ?? profile.DisplayName;
        profile.Locale = locale ?? profile.Locale;

        await database.SaveChangesAsync(cancellationToken);

        var email = await database.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.Email!)
            .FirstAsync(cancellationToken);

        return TypedResults.Ok(new Profile(email, profile.DisplayName, profile.Locale));
    }

    private static bool TryGetUserId(
        HttpContext httpContext,
        UserManager<RekfarUser> users,
        out Guid userId)
    {
        // Read from the cookie's own claim rather than from the store: authentication has
        // already happened, and the security-stamp validator is what decides the claim is
        // still worth believing.
        var value = users.GetUserId(httpContext.User);

        return Guid.TryParse(value, out userId);
    }

    private static ProblemHttpResult NotSignedIn() => TypedResults.Problem(
        title: "Not signed in",
        statusCode: StatusCodes.Status401Unauthorized);

    private static ProblemHttpResult Invalid(string? detail) => TypedResults.Problem(
        title: "Invalid profile",
        detail: detail,
        statusCode: StatusCodes.Status400BadRequest);

    /// <summary>
    /// A session cannot be issued before the profile row exists, so this is unreachable unless
    /// somebody has deleted one behind the API's back. It answers rather than throwing, because
    /// the caller's session is genuinely no longer usable.
    /// </summary>
    private static ProblemHttpResult MissingProfile() => TypedResults.Problem(
        title: "No profile",
        detail: "This account has no profile. Sign in again.",
        statusCode: StatusCodes.Status401Unauthorized);
}
