using Microsoft.AspNetCore.Http;

namespace Rekfar.Accounts;

/// <summary>
/// Deployment-tunable settings for the Auth &amp; Account module.
/// </summary>
/// <remarks>
/// The numbers here are decisions, not defaults that happened: the session clock and the
/// revocation window come from the
/// <see href="https://github.com/rekfar/docs/blob/main/architecture/user-accounts-mvp-plan.md">
/// user-accounts MVP plan §7</see>, and the caps from ADR-0017. They are configurable so a
/// test can collapse a five-minute window to nothing, not because a deployment is expected to
/// disagree with them.
/// </remarks>
public sealed record AccountsOptions
{
    /// <summary>
    /// How long a session lives without use. Ninety days, sliding, renewed on use, with no
    /// absolute cap in Phase 1: passwordless makes an expired session cost an email
    /// round-trip rather than a remembered password, so short sessions would turn routine use
    /// into a delivery gamble.
    /// </summary>
    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromDays(90);

    /// <summary>
    /// How often a live session is re-checked against the user's security stamp. <b>This
    /// interval is the window in which a revoked session still works</b> — five minutes of
    /// it. Traffic is tiny, so the extra store round-trips cost nothing.
    /// </summary>
    public TimeSpan SecurityStampValidationInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// <c>None</c> where the client is not same-site with the API, which is the deployed
    /// shape: the SPA is on Netlify and the API on Azure Container Apps. The cookie is always
    /// <c>Secure</c> and <c>HttpOnly</c>; what keeps <c>None</c> safe is the strict CORS
    /// origin allowlist plus the anti-forgery header the host requires on writes.
    /// </summary>
    public SameSiteMode CookieSameSite { get; init; } = SameSiteMode.None;

    /// <summary>
    /// How many times one address may get a sign-in code wrong before verification stops
    /// answering for it. Reaching this invalidates the outstanding code in the only sense
    /// that matters: no further attempt against that address is checked until the window
    /// passes.
    /// </summary>
    public int MaxVerificationAttempts { get; init; } = 5;

    /// <summary>How long the attempt count above is remembered.</summary>
    public TimeSpan VerificationWindow { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How many codes one address may be sent per <see cref="CodeRequestWindow"/>. Per
    /// address rather than per caller, so a flood aimed at one inbox is stopped even when it
    /// arrives from many clients (NFR-SEC-4).
    /// </summary>
    public int MaxCodeRequestsPerAddress { get; init; } = 5;

    public TimeSpan CodeRequestWindow { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long a used code is remembered, so it cannot be used twice. Must outlast the
    /// window in which Identity would still accept it — fifteen minutes comfortably does.
    /// </summary>
    public TimeSpan UsedCodeMemory { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Requests for a code, per caller, per <see cref="CodeRequestRateLimitWindow"/>. This is
    /// the per-client half of NFR-SEC-4; <see cref="MaxCodeRequestsPerAddress"/> is the other.
    /// Deliberately far below the general <c>/v1</c> limit: nobody signs in ten times an hour.
    /// </summary>
    public int CodeRequestRateLimit { get; init; } = 10;

    public TimeSpan CodeRequestRateLimitWindow { get; init; } = TimeSpan.FromMinutes(15);
}

/// <summary>
/// Where sign-in codes are sent from. Azure Communication Services Email (ADR-0018), reached
/// with a managed identity so there is no API key to store, rotate or leak.
/// </summary>
public sealed record SignInEmailOptions
{
    /// <summary>
    /// The Communication Services resource endpoint, e.g.
    /// <c>https://rekfar-comms.europe.communication.azure.com</c>. Empty means no provider is
    /// configured, which is a startup failure outside development.
    /// </summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>
    /// The <c>From</c> address, on a verified custom domain with SPF and DKIM. Azure's managed
    /// domain sends from a generated <c>azurecomm.net</c> subdomain, which is not acceptable
    /// for mail carrying a login code (ADR-0018).
    /// </summary>
    public string SenderAddress { get; init; } = string.Empty;

    /// <summary>
    /// The identity to authenticate as, named rather than discovered. Empty falls back to
    /// <c>DefaultAzureCredential</c>, which is what a developer signed in with <c>az login</c>
    /// wants; in Azure the client id is set from the template, for the same reason the SQL
    /// connection string names it — no credential-chain probing at startup.
    /// </summary>
    public string ManagedIdentityClientId { get; init; } = string.Empty;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(SenderAddress);
}
