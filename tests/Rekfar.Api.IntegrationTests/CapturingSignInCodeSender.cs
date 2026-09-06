using System.Collections.Concurrent;
using Rekfar.Accounts;

namespace Rekfar.Api.IntegrationTests;

/// <summary>
/// Stands in for Azure Communication Services and keeps what would have been sent.
/// </summary>
/// <remarks>
/// Substituted for the real sender in the test host, which is the one seam these tests need:
/// everything else — Identity, the schema, the cookie, the ledger — is the real thing.
/// Keyed by address so tests that run in parallel against one host cannot read each other's
/// codes; each of them signs in as an address of its own.
/// </remarks>
public sealed class CapturingSignInCodeSender : ISignInCodeSender
{
    private readonly ConcurrentDictionary<string, string> _codes = new(StringComparer.OrdinalIgnoreCase);

    public Task SendAsync(SignInCodeMessage message, CancellationToken cancellationToken)
    {
        _codes[message.EmailAddress] = message.Code;

        return Task.CompletedTask;
    }

    /// <summary>The most recent code sent to an address, or null if none ever was.</summary>
    public string? LatestCodeFor(string emailAddress) =>
        _codes.TryGetValue(emailAddress, out var code) ? code : null;

    public void Forget(string emailAddress) => _codes.TryRemove(emailAddress, out _);
}
