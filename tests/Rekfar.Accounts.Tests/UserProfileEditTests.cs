using Rekfar.Accounts;

namespace Rekfar.Accounts.Tests;

public class UserProfileEditTests
{
    [Fact]
    public void Accepts_a_display_name()
    {
        Assert.True(UserProfileEdit.TryParseDisplayName("Kari Nordmann", out var name, out var error));
        Assert.Equal("Kari Nordmann", name);
        Assert.Null(error);
    }

    /// <summary>
    /// CK_app_User_DisplayName rejects a name that is only whitespace, so trimming here is the
    /// difference between a 400 that says what is wrong and a failed insert that does not.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Rejects_a_display_name_that_is_nothing(string? value)
    {
        Assert.False(UserProfileEdit.TryParseDisplayName(value, out _, out var error));
        Assert.Contains("cannot be empty", error);
    }

    [Fact]
    public void Trims_a_display_name()
    {
        Assert.True(UserProfileEdit.TryParseDisplayName("  Kari  ", out var name, out _));
        Assert.Equal("Kari", name);
    }

    [Fact]
    public void Rejects_a_display_name_longer_than_the_column()
    {
        var tooLong = new string('a', UserProfileEdit.MaxDisplayNameLength + 1);

        Assert.False(UserProfileEdit.TryParseDisplayName(tooLong, out _, out var error));
        Assert.Contains("80", error);
    }

    /// <summary>
    /// The length that matters is the one after trimming, because that is what is stored.
    /// </summary>
    [Fact]
    public void Measures_a_display_name_after_trimming_it()
    {
        var padded = "  " + new string('a', UserProfileEdit.MaxDisplayNameLength) + "  ";

        Assert.True(UserProfileEdit.TryParseDisplayName(padded, out var name, out _));
        Assert.Equal(UserProfileEdit.MaxDisplayNameLength, name.Length);
    }

    [Theory]
    [InlineData("nb-NO")]
    [InlineData("nn-NO")]
    [InlineData("en-GB")]
    [InlineData("en")]
    public void Accepts_a_language_tag_ICU_knows(string value)
    {
        Assert.True(UserProfileEdit.TryParseLocale(value, out var locale, out var error));
        Assert.Equal(value, locale);
        Assert.Null(error);
    }

    /// <summary>
    /// .NET will happily manufacture a CultureInfo for a well-formed tag that names no real
    /// culture, so the check has to ask ICU rather than the parser. A locale nobody has
    /// translations for is a profile field that quietly does nothing.
    /// </summary>
    [Theory]
    [InlineData("xx-YY")]
    [InlineData("not a locale")]
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_a_tag_that_names_no_culture(string? value)
    {
        Assert.False(UserProfileEdit.TryParseLocale(value, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Rejects_a_locale_longer_than_the_column()
    {
        Assert.False(UserProfileEdit.TryParseLocale(new string('a', 32), out _, out var error));
        Assert.Contains("16", error);
    }

    [Fact]
    public void Defaults_are_the_ones_the_columns_default_to()
    {
        Assert.Equal("nb-NO", UserProfileEdit.DefaultLocale);
        Assert.Equal("private", UserProfileEdit.DefaultPrivacy);
    }
}
