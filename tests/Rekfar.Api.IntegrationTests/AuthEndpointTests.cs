using System.Net;
using Microsoft.Data.SqlClient;

namespace Rekfar.Api.IntegrationTests;

[Collection(nameof(RekfarApiCollection))]
public class AuthEndpointTests(RekfarApiFixture fixture)
{
    private const string SessionCookie = "__Host-rekfar.session";

    /// <summary>
    /// The whole of FR-ACC-1 and FR-ACC-2 in one pass: an address nobody has used before is an
    /// account after one code, with no password anywhere in the exchange.
    /// </summary>
    [Fact]
    public async Task An_unknown_address_becomes_an_account_by_email_code_alone()
    {
        var email = TestAccount.NewAddress();
        var client = fixture.CreateClient();

        var signedIn = await TestAccount.ReadJsonAsync(
            await TestAccount.SignInAsync(fixture, client, email));

        Assert.Equal(email, signedIn.GetProperty("email").GetString());
        Assert.Equal("nb-NO", signedIn.GetProperty("locale").GetString());

        // The session is real, not just a 200: the next request is authenticated by the cookie.
        var me = await client.GetAsync("/v1/me");

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    /// <summary>
    /// The mapping this module exists to get right. Identity's row lands in <c>auth.[User]</c>
    /// and the profile in <c>app.[User]</c>, sharing one primary key — asserted against the
    /// real schema, because the schema belongs to another repository and a mapping that has
    /// drifted from it is exactly what a hand-built approximation would hide.
    /// </summary>
    [Fact]
    public async Task Verifying_writes_a_confirmed_identity_row_and_a_profile_sharing_its_key()
    {
        var email = TestAccount.NewAddress();

        await TestAccount.SignInAsync(fixture, fixture.CreateClient(), email);

        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT identity_row.EmailConfirmed, identity_row.NormalizedEmail, profile.DisplayName,
                   profile.Locale, profile.DefaultPrivacy
            FROM auth.[User] AS identity_row
            INNER JOIN app.[User] AS profile ON profile.Id = identity_row.Id
            WHERE identity_row.Email = @email;
            """;
        command.Parameters.AddWithValue("@email", email);

        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync(), "No joined auth/app row for the new account.");

        // Verifying the code IS the email confirmation (ADR-0017) — there is no second link.
        Assert.True(reader.GetBoolean(0));
        Assert.Equal(email.ToUpperInvariant(), reader.GetString(1));

        // Seeded from the local part, because DisplayName is NOT NULL and nothing else is known
        // about the person yet.
        Assert.Equal(email[..email.IndexOf('@', StringComparison.Ordinal)], reader.GetString(2));
        Assert.Equal("nb-NO", reader.GetString(3));
        Assert.Equal("private", reader.GetString(4));
    }

    /// <summary>
    /// The endpoint must not become an oracle for which addresses have accounts. A known and an
    /// unknown address are indistinguishable in the response.
    /// </summary>
    [Fact]
    public async Task Requesting_a_code_answers_the_same_way_for_a_known_and_an_unknown_address()
    {
        var client = fixture.CreateClient();
        var known = TestAccount.NewAddress();

        await TestAccount.SignInAsync(fixture, client, known);

        var forKnown = await TestAccount.RequestCodeAsync(client, known);
        var forUnknown = await TestAccount.RequestCodeAsync(client, TestAccount.NewAddress());

        Assert.Equal(HttpStatusCode.Accepted, forKnown.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, forUnknown.StatusCode);
        Assert.Equal(forKnown.Content.Headers.ContentLength, forUnknown.Content.Headers.ContentLength);
    }

    /// <summary>
    /// The one thing the endpoint will refuse, because it is a fact about the request rather
    /// than about an inbox.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("kari@example")]
    public async Task Rejects_a_malformed_address_as_problem_details(string email)
    {
        var response = await TestAccount.RequestCodeAsync(fixture.CreateClient(), email);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await TestAccount.ReadJsonAsync(response);

        Assert.True(problem.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task A_wrong_code_does_not_sign_anybody_in()
    {
        var email = TestAccount.NewAddress();
        var client = fixture.CreateClient();

        await TestAccount.RequestCodeAsync(client, email);

        var response = await TestAccount.VerifyAsync(client, email, "000000");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/me")).StatusCode);
    }

    /// <summary>
    /// Single-use (ADR-0017). Identity's TOTP code would otherwise stay valid for the rest of
    /// its window, so replaying one somebody read over a shoulder has to be refused explicitly.
    /// </summary>
    [Fact]
    public async Task A_code_signs_somebody_in_once_and_then_never_again()
    {
        var email = TestAccount.NewAddress();
        var first = fixture.CreateClient();

        await TestAccount.RequestCodeAsync(first, email);
        var code = fixture.Emails.LatestCodeFor(email)!;

        Assert.Equal(HttpStatusCode.OK, (await TestAccount.VerifyAsync(first, email, code)).StatusCode);

        var replay = await TestAccount.VerifyAsync(fixture.CreateClient(), email, code);

        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    /// <summary>
    /// Capped at a small number of attempts, and the cap invalidates the code rather than only
    /// the guesses: the right code stops being looked at too.
    /// </summary>
    [Fact]
    public async Task Stops_answering_for_an_address_after_too_many_wrong_codes()
    {
        var email = TestAccount.NewAddress();
        var client = fixture.CreateClient();

        await TestAccount.RequestCodeAsync(client, email);
        var code = fixture.Emails.LatestCodeFor(email)!;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.Equal(
                HttpStatusCode.Unauthorized,
                (await TestAccount.VerifyAsync(client, email, "000000")).StatusCode);
        }

        var blocked = await TestAccount.VerifyAsync(client, email, code);

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
    }

    /// <summary>
    /// Per address, not per caller (NFR-SEC-4). Someone hammering the endpoint with one
    /// person's address stops being able to fill their inbox — while still being told nothing.
    /// </summary>
    [Fact]
    public async Task Stops_sending_once_an_address_has_had_its_allowance()
    {
        var email = TestAccount.NewAddress();
        var client = fixture.CreateClient();

        for (var request = 0; request < 5; request++)
        {
            Assert.Equal(
                HttpStatusCode.Accepted,
                (await TestAccount.RequestCodeAsync(client, email)).StatusCode);
        }

        fixture.Emails.Forget(email);

        var beyond = await TestAccount.RequestCodeAsync(client, email);

        // Still 202 — saying otherwise would confirm the address to a stranger — but nothing
        // left the building.
        Assert.Equal(HttpStatusCode.Accepted, beyond.StatusCode);
        Assert.Null(fixture.Emails.LatestCodeFor(email));
    }

    [Fact]
    public async Task Signing_out_ends_the_session_on_this_device()
    {
        var client = fixture.CreateClient();

        await TestAccount.SignInAsync(fixture, client, TestAccount.NewAddress());

        var signedOut = await client.PostAsync("/v1/auth/signout", content: null);

        Assert.Equal(HttpStatusCode.NoContent, signedOut.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/me")).StatusCode);
    }

    /// <summary>
    /// With no password to change, rotating the security stamp is the only lever a user has
    /// over a device they no longer hold — so this asserts the half that matters: a session
    /// <em>other</em> than the one that asked stops working.
    /// </summary>
    /// <remarks>
    /// The other device holds a copy of the same cookie rather than one from a second sign-in.
    /// It cannot be a second sign-in: a code is single-use, and the next one is a few minutes
    /// away. What is being revoked is the security stamp every session is validated against,
    /// and a copied cookie is validated by exactly the same path a second device's would be.
    ///
    /// It also depends on the validation interval, which the fixture sets to zero. In the
    /// deployment it is five minutes, and those five minutes are the window in which a revoked
    /// session still works.
    /// </remarks>
    [Fact]
    public async Task Signing_out_everywhere_ends_a_session_on_another_device()
    {
        var email = TestAccount.NewAddress();
        var thisDevice = fixture.CreateClient();

        var session = SessionCookieFrom(await TestAccount.SignInAsync(fixture, thisDevice, email));

        var otherDevice = fixture.CreateClient(handleCookies: false);
        otherDevice.DefaultRequestHeaders.Add("Cookie", session);

        Assert.Equal(HttpStatusCode.OK, (await otherDevice.GetAsync("/v1/me")).StatusCode);

        var revoked = await thisDevice.PostAsync("/v1/auth/signout-all", content: null);

        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await otherDevice.GetAsync("/v1/me")).StatusCode);
    }

    /// <summary>
    /// One account per address (FR-ACC-1). The second request finds the row the first one
    /// created rather than making another; UX_auth_User_NormalizedEmail is what would stop it
    /// if the lookup were wrong, and a failed insert is not the behaviour to settle for.
    /// </summary>
    [Fact]
    public async Task Asking_for_a_second_code_does_not_create_a_second_account()
    {
        var email = TestAccount.NewAddress();
        var client = fixture.CreateClient();

        await TestAccount.SignInAsync(fixture, client, email);

        Assert.Equal(
            HttpStatusCode.Accepted,
            (await TestAccount.RequestCodeAsync(client, email)).StatusCode);

        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM auth.[User] WHERE Email = @email;";
        command.Parameters.AddWithValue("@email", email);

        Assert.Equal(1, (int)(await command.ExecuteScalarAsync())!);
    }

    /// <summary>
    /// The session cookie is SameSite=None, so the browser would attach it to a cross-site form
    /// post. The required header is what a form post cannot produce.
    /// </summary>
    [Fact]
    public async Task A_state_changing_request_without_the_anti_forgery_header_is_refused()
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Remove("X-Rekfar-Csrf");

        var response = await TestAccount.RequestCodeAsync(client, TestAccount.NewAddress());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>The catalogue is anonymous by design; the same header requirement must not touch it.</summary>
    [Fact]
    public async Task Reading_the_catalogue_still_needs_no_header_and_no_account()
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Remove("X-Rekfar-Csrf");

        var response = await client.GetAsync("/v1/peaks?bbox=7.5,61.3,8.8,61.8");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static string SessionCookieFrom(HttpResponseMessage response)
    {
        var setCookie = response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith($"{SessionCookie}=", StringComparison.Ordinal));

        // Just the name=value pair; the attributes after it are the browser's business.
        var end = setCookie.IndexOf(';', StringComparison.Ordinal);

        return end < 0 ? setCookie : setCookie[..end];
    }
}
