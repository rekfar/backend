using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Rekfar.Accounts;

/// <summary>
/// Sign-in and sign-out. Mapped by the host into its versioned group.
/// </summary>
/// <remarks>
/// Registration and login are the same flow (ADR-0017): an address nobody has used yet gets
/// an account the first time somebody proves they can read it. There is no register endpoint
/// and no password field anywhere.
/// </remarks>
public static class AuthEndpoints
{
    /// <summary>
    /// Names the token this code is for. Identity mixes it into the derivation, so a code
    /// issued for signing in cannot be replayed against a future purpose — an email-change
    /// confirmation, say — that happens to use the same provider.
    /// </summary>
    private const string SignInPurpose = "rekfar:signin";

    /// <summary>
    /// Six digits from Identity's TOTP provider. Bounded here only so an oversized body is
    /// rejected before it reaches a hash.
    /// </summary>
    private const int MaxCodeLength = 16;

    /// <summary>
    /// One message for every way verification can fail: wrong code, expired code, a code
    /// already used, an address with no account. Distinguishing them would tell an
    /// unauthenticated caller which addresses exist.
    /// </summary>
    private const string RejectedDetail =
        "That code is not valid. It may have expired, or already been used. Ask for a new one.";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder builder)
    {
        var auth = builder.MapGroup("/auth").WithTags("Auth");

        auth.MapPost("/code", RequestCode)
            .WithName("RequestSignInCode")
            .WithSummary("Send a sign-in code to an email address")
            .WithDescription(
                "Emails a one-time code. Answers 202 whether or not the address has an "
                + "account, and whether or not a code was actually sent — anything else would "
                + "tell an anonymous caller which addresses are registered. An address with no "
                + "account gets one the first time it verifies a code.")

            // Its own budget, far below the general /v1 limit: this endpoint sends email on an
            // anonymous caller's say-so (NFR-SEC-4).
            .RequireRateLimiting(AccountsRateLimitPolicies.SignInCode)
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        auth.MapPost("/verify", Verify)
            .WithName("VerifySignInCode")
            .WithSummary("Exchange a sign-in code for a session")
            .WithDescription(
                "On success sets the session cookie and returns the profile, so a client that "
                + "has just signed in does not need a second round trip to render itself. "
                + "Creates the account if this is the address's first successful verification.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        auth.MapPost("/signout", SignOutThisDevice)
            .WithName("SignOut")
            .WithSummary("End this session")
            .RequireAuthorization()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        auth.MapPost("/signout-all", SignOutEverywhere)
            .WithName("SignOutEverywhere")
            .WithSummary("End every session, on every device")
            .WithDescription(
                "Rotates the security stamp, which invalidates every outstanding session and "
                + "every unused sign-in code. Other devices stop working within the "
                + "security-stamp validation interval rather than instantly. With no password "
                + "to change, this is the only lever a user has over a lost device.")
            .RequireAuthorization()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        return builder;
    }

    private static async Task<Results<StatusCodeHttpResult, ProblemHttpResult>> RequestCode(
        SignInCodeRequest request,
        UserManager<RekfarUser> users,
        SignInCodeLedger ledger,
        ISignInCodeSender sender,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(typeof(AuthEndpoints));

        if (!UserEmail.TryParse(request.Email, out var email, out var error))
        {
            // The one thing this endpoint will say no to, because it is about the request
            // rather than about the account: a malformed address is the caller's own typo, and
            // it reveals nothing.
            return TypedResults.Problem(
                title: "Invalid email address",
                detail: error,
                statusCode: StatusCodes.Status400BadRequest);
        }

        // From here on the answer is 202 whatever happens. Every branch below is a fact about
        // one inbox, and none of them is this caller's to learn.
        if (!ledger.TryClaimCodeRequest(email))
        {
            logger.LogInformation(
                "A sign-in code was not sent: the address has reached its request limit.");

            return Accepted();
        }

        var user = await users.FindByEmailAsync(email);

        if (user is null)
        {
            // The row has to exist before a code can be derived: Identity's provider derives it
            // from this user's security stamp. It is not an account yet — EmailConfirmed is
            // false and there is no profile row — and it becomes one on first verification.
            user = new RekfarUser { UserName = email, Email = email };

            var created = await users.CreateAsync(user);

            if (!created.Succeeded)
            {
                logger.LogWarning(
                    "Could not create a user row while sending a sign-in code: {Errors}",
                    string.Join("; ", created.Errors.Select(failure => failure.Code)));

                return Accepted();
            }
        }

        var code = await users.GenerateUserTokenAsync(
            user, TokenOptions.DefaultEmailProvider, SignInPurpose);

        // Deliberately not swallowed. A provider outage is not something to hide behind a 202:
        // the caller would sit waiting for an email that is never coming, and the host turns
        // this into the same ProblemDetails shape as any other failure.
        await sender.SendAsync(new SignInCodeMessage(email, code), cancellationToken);

        return Accepted();
    }

    private static async Task<Results<Ok<Profile>, ProblemHttpResult>> Verify(
        SignInVerification request,
        UserManager<RekfarUser> users,
        SignInManager<RekfarUser> signIn,
        SignInCodeLedger ledger,
        AccountsDbContext database,
        CancellationToken cancellationToken)
    {
        if (!UserEmail.TryParse(request.Email, out var email, out var error))
        {
            return TypedResults.Problem(
                title: "Invalid email address",
                detail: error,
                statusCode: StatusCodes.Status400BadRequest);
        }

        var code = request.Code?.Trim() ?? string.Empty;

        if (code.Length == 0 || code.Length > MaxCodeLength)
        {
            return TypedResults.Problem(
                title: "Invalid code",
                detail: "A sign-in code is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var verdict = ledger.BeginVerification(email, code);

        if (verdict is SignInCodeVerdict.TooManyAttempts)
        {
            return TooManyAttempts(ledger.RetryAfter(email));
        }

        if (verdict is SignInCodeVerdict.AlreadyUsed)
        {
            return Rejected();
        }

        var user = await users.FindByEmailAsync(email);

        if (user is null)
        {
            return Rejected();
        }

        var verified = await users.VerifyUserTokenAsync(
            user, TokenOptions.DefaultEmailProvider, SignInPurpose, code);

        if (!verified)
        {
            return Rejected();
        }

        ledger.MarkUsed(email, code);

        // Verifying the code *is* the email confirmation (ADR-0017). There is no second link
        // to click, and this is the moment the address is proven.
        if (!user.EmailConfirmed)
        {
            user.EmailConfirmed = true;
            await users.UpdateAsync(user);
        }

        var profile = await database.Profiles
            .FirstOrDefaultAsync(row => row.Id == user.Id, cancellationToken);

        if (profile is null)
        {
            // First successful verification: this is where the account is created, not where
            // the code was requested (FR-ACC-1).
            profile = new UserProfile
            {
                Id = user.Id,
                DisplayName = UserEmail.ToDisplayName(email),
                Locale = UserProfileEdit.DefaultLocale,
                DefaultPrivacy = UserProfileEdit.DefaultPrivacy,
            };

            database.Profiles.Add(profile);
            await database.SaveChangesAsync(cancellationToken);
        }

        // Persistent, so closing the browser does not end the session. With no password to
        // retype, an expired session costs an email round-trip — see the MVP plan §7.
        await signIn.SignInAsync(user, isPersistent: true);

        return TypedResults.Ok(new Profile(user.Email!, profile.DisplayName, profile.Locale));
    }

    private static async Task<NoContent> SignOutThisDevice(SignInManager<RekfarUser> signIn)
    {
        // Server-side, not just a cleared cookie: the session is gone whether or not the client
        // co-operates in forgetting it (FR-ACC-2).
        await signIn.SignOutAsync();

        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> SignOutEverywhere(
        ClaimsPrincipal principal,
        UserManager<RekfarUser> users,
        SignInManager<RekfarUser> signIn)
    {
        var user = await users.GetUserAsync(principal);

        if (user is null)
        {
            return TypedResults.Problem(
                title: "Not signed in",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        // One call, and the only one a user has. Every cookie carrying the old stamp fails its
        // next validation, and every code derived from it stops verifying.
        await users.UpdateSecurityStampAsync(user);

        // This device does not have to wait for that: clearing its own cookie now means the
        // validation interval is the window for the *other* devices, not for this one.
        await signIn.SignOutAsync();

        return TypedResults.NoContent();
    }

    /// <summary>
    /// 202, with no <c>Location</c>: there is no status monitor to point a client at, and
    /// there is deliberately nothing to say about what happened to the address.
    /// </summary>
    private static StatusCodeHttpResult Accepted() =>
        TypedResults.StatusCode(StatusCodes.Status202Accepted);

    private static ProblemHttpResult Rejected() => TypedResults.Problem(
        title: "Sign-in failed",
        detail: RejectedDetail,
        statusCode: StatusCodes.Status401Unauthorized);

    private static ProblemHttpResult TooManyAttempts(TimeSpan retryAfter) => TypedResults.Problem(
        title: "Too many attempts",
        detail: "Too many codes have been tried for this address. Wait, then ask for a new one.",
        statusCode: StatusCodes.Status429TooManyRequests,
        extensions: new Dictionary<string, object?>
        {
            ["retryAfterSeconds"] = (int)Math.Ceiling(retryAfter.TotalSeconds),
        });
}
