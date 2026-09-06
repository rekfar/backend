using Rekfar.Accounts;

namespace Rekfar.Accounts.Tests;

public class UserEmailTests
{
    [Theory]
    [InlineData("kari@example.no")]
    [InlineData("kari.nordmann+tur@example.co.uk")]
    [InlineData("k@fjell.no")]
    public void Accepts_an_address(string value)
    {
        Assert.True(UserEmail.TryParse(value, out var email, out var error));
        Assert.Equal(value, email);
        Assert.Null(error);
    }

    [Fact]
    public void Trims_surrounding_whitespace()
    {
        Assert.True(UserEmail.TryParse("  kari@example.no\t", out var email, out _));
        Assert.Equal("kari@example.no", email);
    }

    /// <summary>
    /// The local part is case-sensitive by the specification. Identity's upper-cased
    /// NormalizedEmail is what makes lookup case-insensitive, so nothing here has to lower it —
    /// and lowering it would send the code to an address the user did not type.
    /// </summary>
    [Fact]
    public void Keeps_the_case_it_was_given()
    {
        Assert.True(UserEmail.TryParse("Kari.Nordmann@Example.NO", out var email, out _));
        Assert.Equal("Kari.Nordmann@Example.NO", email);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("kari")]
    [InlineData("kari@")]
    [InlineData("@example.no")]
    [InlineData("kari@example")]
    [InlineData("kari nordmann@example.no")]
    // MailAddress parses this as a display name plus an address. It is not what anybody typed
    // into a sign-in box, and accepting it would put a header-shaped string in a From field.
    [InlineData("Kari <kari@example.no>")]
    public void Rejects_what_is_not_an_address(string? value)
    {
        Assert.False(UserEmail.TryParse(value, out var email, out var error));
        Assert.Equal(string.Empty, email);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Rejects_an_address_longer_than_the_column()
    {
        var tooLong = new string('a', UserEmail.MaxLength) + "@example.no";

        Assert.False(UserEmail.TryParse(tooLong, out _, out var error));
        Assert.Contains("256", error);
    }

    [Fact]
    public void Seeds_a_display_name_from_the_local_part()
    {
        Assert.Equal("kari.nordmann", UserEmail.ToDisplayName("kari.nordmann@example.no"));
    }

    /// <summary>
    /// app.[User].DisplayName is nvarchar(80). A local part longer than that would fail the
    /// insert on the one request where a user has just proved who they are.
    /// </summary>
    [Fact]
    public void Truncates_a_seeded_display_name_to_the_column()
    {
        var name = UserEmail.ToDisplayName(new string('a', 200) + "@example.no");

        Assert.Equal(UserProfileEdit.MaxDisplayNameLength, name.Length);
    }
}
