using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Rekfar.Api.IntegrationTests;

/// <summary>
/// Driving the sign-in flow the way a client does: request a code, read it out of the mail,
/// send it back.
/// </summary>
/// <remarks>
/// Every test takes an address of its own. The caps on codes and guesses are per address, so
/// sharing one would make the tests interfere in ways that look like flakiness.
/// </remarks>
internal static class TestAccount
{
    public static string NewAddress() => $"{Guid.NewGuid():n}@rekfar.test";

    public static Task<HttpResponseMessage> RequestCodeAsync(HttpClient client, string email) =>
        client.PostAsJsonAsync("/v1/auth/code", new { email });

    public static Task<HttpResponseMessage> VerifyAsync(HttpClient client, string email, string code) =>
        client.PostAsJsonAsync("/v1/auth/verify", new { email, code });

    /// <summary>Requests a code, reads it out of the captured mail, and signs in with it.</summary>
    public static async Task<HttpResponseMessage> SignInAsync(
        RekfarApiFixture fixture,
        HttpClient client,
        string email)
    {
        var requested = await RequestCodeAsync(client, email);

        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);

        var code = fixture.Emails.LatestCodeFor(email);

        Assert.NotNull(code);

        var verified = await VerifyAsync(client, email, code);

        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);

        return verified;
    }

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
}
