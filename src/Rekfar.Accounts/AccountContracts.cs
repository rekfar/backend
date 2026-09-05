namespace Rekfar.Accounts;

/// <summary>Ask for a sign-in code. The only field an account starts with.</summary>
public sealed record SignInCodeRequest(string? Email);

/// <summary>Exchange a code for a session.</summary>
public sealed record SignInVerification(string? Email, string? Code);

/// <summary>
/// Change the profile. Both fields are optional; a field that is absent or null is left as it
/// is, which is what makes this a PATCH rather than a PUT.
/// </summary>
public sealed record ProfileUpdate(string? DisplayName, string? Locale);

/// <summary>The signed-in user, as <c>/v1/me</c> returns them.</summary>
/// <remarks>
/// Three fields, because that is what an account is (P9, NFR-PRIV-2). Default privacy is
/// FR-ACC-4 and arrives with trips; it is not on the wire until something can act on it.
/// </remarks>
public sealed record Profile(string Email, string DisplayName, string Locale);
