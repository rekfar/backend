using Microsoft.Extensions.Logging;

namespace Rekfar.Accounts;

/// <summary>
/// Writes the sign-in code to the log instead of sending it.
/// </summary>
/// <remarks>
/// For local development, where standing up a verified sending domain to try a sign-in would
/// be absurd. It is chosen only when no email provider is configured, and
/// <see cref="AccountsModule"/> refuses to start with it outside a development environment —
/// a login code in a production log is a credential in a log.
/// </remarks>
internal sealed class LogOnlySignInCodeSender(ILogger<LogOnlySignInCodeSender> logger)
    : ISignInCodeSender
{
    public Task SendAsync(SignInCodeMessage message, CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "No email provider is configured, so no email was sent. The sign-in code for "
            + "{EmailAddress} is {Code}.",
            message.EmailAddress,
            message.Code);

        return Task.CompletedTask;
    }
}
