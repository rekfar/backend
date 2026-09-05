using System.Globalization;

namespace Rekfar.Accounts;

/// <summary>
/// Validation for the two profile fields a user can change (FR-ACC-3).
/// </summary>
/// <remarks>
/// Both rules mirror a constraint the schema already enforces, and both are here as well
/// because a CHECK constraint violation reaches the caller as a 500 naming a constraint —
/// which is neither an explanation nor something a client can act on.
/// </remarks>
public static class UserProfileEdit
{
    /// <summary><c>app.[User].DisplayName</c> is <c>nvarchar(80)</c>.</summary>
    public const int MaxDisplayNameLength = 80;

    /// <summary><c>app.[User].Locale</c> is <c>varchar(16)</c>.</summary>
    public const int MaxLocaleLength = 16;

    /// <summary>The first locale (ADR-0003), and the column's own default.</summary>
    public const string DefaultLocale = "nb-NO";

    /// <summary>The column's default, and the only value FR-ACC-4 will start from.</summary>
    public const string DefaultPrivacy = "private";

    public static bool TryParseDisplayName(string? value, out string displayName, out string? error)
    {
        displayName = string.Empty;

        // CK_app_User_DisplayName rejects a name that is only whitespace, so trimming here is
        // not tidying — it is the difference between a 400 and a failed insert.
        var candidate = value?.Trim() ?? string.Empty;

        if (candidate.Length == 0)
        {
            error = "'displayName' cannot be empty.";
            return false;
        }

        if (candidate.Length > MaxDisplayNameLength)
        {
            error = $"'displayName' may be at most {MaxDisplayNameLength} characters.";
            return false;
        }

        displayName = candidate;
        error = null;
        return true;
    }

    public static bool TryParseLocale(string? value, out string locale, out string? error)
    {
        locale = string.Empty;

        var candidate = value?.Trim() ?? string.Empty;

        if (candidate.Length == 0)
        {
            error = "'locale' cannot be empty.";
            return false;
        }

        if (candidate.Length > MaxLocaleLength)
        {
            error = $"'locale' may be at most {MaxLocaleLength} characters.";
            return false;
        }

        // predefinedOnly, so ICU is asked whether the tag names a real culture rather than
        // whether it is well-formed: .NET happily manufactures a CultureInfo for "xx-YY". The
        // application ships ICU on purpose (Directory.Build.props), so this is a real check.
        try
        {
            CultureInfo.GetCultureInfo(candidate, predefinedOnly: true);
        }
        catch (CultureNotFoundException)
        {
            error = $"'{candidate}' is not a language tag this server knows.";
            return false;
        }

        locale = candidate;
        error = null;
        return true;
    }
}
