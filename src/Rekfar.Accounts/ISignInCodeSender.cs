namespace Rekfar.Accounts;

/// <summary>A sign-in code on its way to one address.</summary>
public sealed record SignInCodeMessage(string EmailAddress, string Code);

/// <summary>
/// Sends the one email this product sends.
/// </summary>
/// <remarks>
/// Small on purpose. ADR-0018 chose Azure Communication Services and named Scaleway TEM as the
/// fallback, on the condition that switching costs one class — this interface is that
/// condition, written down.
/// </remarks>
public interface ISignInCodeSender
{
    Task SendAsync(SignInCodeMessage message, CancellationToken cancellationToken);
}
