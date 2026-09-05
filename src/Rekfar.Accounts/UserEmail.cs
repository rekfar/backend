using System.Net.Mail;

namespace Rekfar.Accounts;

/// <summary>
/// The one credential Rekfar has. Everything an account is hangs off it, so it is checked
/// here rather than trusted from a client.
/// </summary>
public static class UserEmail
{
    /// <summary><c>auth.[User].Email</c> is <c>nvarchar(256)</c>.</summary>
    public const int MaxLength = 256;

    /// <summary>
    /// Trims and validates an address as typed, returning a caller-facing reason on failure.
    /// </summary>
    /// <remarks>
    /// Deliberately not a full RFC 5322 parse. What this has to catch is a typo or a junk
    /// value before an account row is created for it and a code is sent into the void — the
    /// address's real validity is decided by whether anybody reads the code.
    ///
    /// Case is preserved. The local part is case-sensitive by the specification even though
    /// nobody treats it that way; Identity's own upper-cased <c>NormalizedEmail</c> is what
    /// makes lookup and the one-account-per-address rule case-insensitive.
    /// </remarks>
    public static bool TryParse(string? value, out string email, out string? error)
    {
        email = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "An email address is required.";
            return false;
        }

        var candidate = value.Trim();

        if (candidate.Length > MaxLength)
        {
            error = $"An email address may be at most {MaxLength} characters.";
            return false;
        }

        // MailAddress also accepts a display name ("Kari <kari@example.no>"), which is not an
        // address anyone typed into a sign-in box. Comparing what it parsed against what came
        // in rejects that without a second parser.
        if (!MailAddress.TryCreate(candidate, out var parsed)
            || !string.Equals(parsed.Address, candidate, StringComparison.Ordinal)
            || !parsed.Host.Contains('.', StringComparison.Ordinal))
        {
            error = "That is not an email address.";
            return false;
        }

        email = candidate;
        error = null;
        return true;
    }

    /// <summary>
    /// A first display name for a brand-new account. <c>app.[User].DisplayName</c> is NOT NULL
    /// with a non-empty CHECK, and at the moment an account is created the address is the only
    /// thing known about its owner — so the local part stands in until they rename themselves.
    /// </summary>
    public static string ToDisplayName(string email)
    {
        var at = email.IndexOf('@', StringComparison.Ordinal);
        var local = at > 0 ? email[..at] : email;

        return local.Length > UserProfileEdit.MaxDisplayNameLength
            ? local[..UserProfileEdit.MaxDisplayNameLength]
            : local;
    }
}
