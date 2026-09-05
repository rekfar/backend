using System.Net;
using System.Net.Http.Json;

namespace Rekfar.Api.IntegrationTests;

[Collection(nameof(RekfarApiCollection))]
public class MeEndpointTests(RekfarApiFixture fixture)
{
    private async Task<HttpClient> SignedInAsync(string email)
    {
        var client = fixture.CreateClient();
        await TestAccount.SignInAsync(fixture, client, email);

        return client;
    }

    [Fact]
    public async Task Returns_the_signed_in_user()
    {
        var email = TestAccount.NewAddress();
        var client = await SignedInAsync(email);

        var me = await TestAccount.ReadJsonAsync(await client.GetAsync("/v1/me"));

        Assert.Equal(email, me.GetProperty("email").GetString());
        Assert.Equal(email[..email.IndexOf('@', StringComparison.Ordinal)], me.GetProperty("displayName").GetString());
        Assert.Equal("nb-NO", me.GetProperty("locale").GetString());
    }

    [Fact]
    public async Task Changes_the_display_name()
    {
        var client = await SignedInAsync(TestAccount.NewAddress());

        var updated = await client.PatchAsJsonAsync("/v1/me", new { displayName = "  Kari Nordmann  " });

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        // Trimmed, because CK_app_User_DisplayName is about the stored value.
        Assert.Equal(
            "Kari Nordmann",
            (await TestAccount.ReadJsonAsync(updated)).GetProperty("displayName").GetString());

        var reread = await TestAccount.ReadJsonAsync(await client.GetAsync("/v1/me"));

        Assert.Equal("Kari Nordmann", reread.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Changes_the_locale()
    {
        var client = await SignedInAsync(TestAccount.NewAddress());

        var updated = await TestAccount.ReadJsonAsync(
            await client.PatchAsJsonAsync("/v1/me", new { locale = "nn-NO" }));

        Assert.Equal("nn-NO", updated.GetProperty("locale").GetString());
    }

    /// <summary>
    /// What makes this a PATCH: a field the client did not send is not a field the client
    /// cleared. Sending only a locale must not wipe the display name.
    /// </summary>
    [Fact]
    public async Task Leaves_out_what_the_client_left_out()
    {
        var client = await SignedInAsync(TestAccount.NewAddress());

        await client.PatchAsJsonAsync("/v1/me", new { displayName = "Ola" });

        var updated = await TestAccount.ReadJsonAsync(
            await client.PatchAsJsonAsync("/v1/me", new { locale = "nn-NO" }));

        Assert.Equal("Ola", updated.GetProperty("displayName").GetString());
        Assert.Equal("nn-NO", updated.GetProperty("locale").GetString());
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public async Task Rejects_a_display_name_that_is_nothing(string displayName)
    {
        var client = await SignedInAsync(TestAccount.NewAddress());

        var response = await client.PatchAsJsonAsync("/v1/me", new { displayName });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Rejects_a_display_name_longer_than_the_column()
    {
        var client = await SignedInAsync(TestAccount.NewAddress());

        var response = await client.PatchAsJsonAsync(
            "/v1/me", new { displayName = new string('a', 81) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_a_language_tag_that_names_no_culture()
    {
        var client = await SignedInAsync(TestAccount.NewAddress());

        var response = await client.PatchAsJsonAsync("/v1/me", new { locale = "xx-YY" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Neither field is applied until both have validated, so a rejected locale cannot leave a
    /// new display name behind it.
    /// </summary>
    [Fact]
    public async Task Applies_nothing_when_one_field_is_invalid()
    {
        var email = TestAccount.NewAddress();
        var client = await SignedInAsync(email);

        await client.PatchAsJsonAsync(
            "/v1/me", new { displayName = "Kari", locale = "xx-YY" });

        var me = await TestAccount.ReadJsonAsync(await client.GetAsync("/v1/me"));

        Assert.Equal(email[..email.IndexOf('@', StringComparison.Ordinal)], me.GetProperty("displayName").GetString());
    }

    /// <summary>
    /// NFR-SEC-3: every user-data endpoint requires authentication. The catalogue is the one
    /// surface that does not, and it is anonymous on purpose.
    /// </summary>
    [Fact]
    public async Task Refuses_an_anonymous_read_as_problem_details()
    {
        var response = await fixture.CreateClient().GetAsync("/v1/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await TestAccount.ReadJsonAsync(response);

        Assert.True(problem.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task Refuses_an_anonymous_write()
    {
        var response = await fixture.CreateClient()
            .PatchAsJsonAsync("/v1/me", new { displayName = "Kari" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
