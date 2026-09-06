using Azure;
using Azure.Communication.Email;
using Microsoft.Extensions.Logging;

namespace Rekfar.Accounts;

/// <summary>
/// Sends sign-in codes through Azure Communication Services Email (ADR-0018).
/// </summary>
/// <remarks>
/// The client is authenticated by managed identity, so no key or connection string exists in
/// the deployment — the same structural answer the SQL connection uses. Nothing here logs the
/// recipient or the code: the operation id is enough to find a send in the provider's records,
/// and a login code has no business in a log line.
/// </remarks>
internal sealed class AcsSignInCodeSender(
    EmailClient client,
    SignInEmailOptions options,
    ILogger<AcsSignInCodeSender> logger) : ISignInCodeSender
{
    public async Task SendAsync(SignInCodeMessage message, CancellationToken cancellationToken)
    {
        var content = new EmailContent(SignInCodeEmail.Subject)
        {
            PlainText = SignInCodeEmail.PlainText(message.Code),
            Html = SignInCodeEmail.Html(message.Code),
        };

        // WaitUntil.Started, not Completed: a person is waiting on this request, and what they
        // are waiting for is the API to accept the send — not for the provider's pipeline to
        // report a delivery it can only know about later anyway.
        var operation = await client.SendAsync(
            WaitUntil.Started,
            new EmailMessage(options.SenderAddress, message.EmailAddress, content),
            cancellationToken);

        logger.LogInformation(
            "Queued a sign-in code with Azure Communication Services (operation {OperationId}).",
            operation.Id);
    }
}
