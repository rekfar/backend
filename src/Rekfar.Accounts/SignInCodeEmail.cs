namespace Rekfar.Accounts;

/// <summary>
/// The text of the sign-in email, in Norwegian.
/// </summary>
/// <remarks>
/// User-facing strings are Norwegian with <c>nb-NO</c> as the first locale (ADR-0003). There
/// is one message and no user-supplied text in it — not even the recipient's own address —
/// which is both why it needs no template engine and why nothing here has to be escaped.
///
/// The code is deliberately <b>not</b> in the subject. It would show on a lock screen, and
/// this is the mail whose whole purpose is to be worth trusting.
/// </remarks>
public static class SignInCodeEmail
{
    public const string Subject = "Innloggingskode til Rekfar";

    public static string PlainText(string code) => $"""
        Innloggingskoden din til Rekfar er:

        {code}

        Koden er gyldig i omtrent ti minutter og kan bare brukes én gang.

        Ba du ikke om å logge inn? Da kan du se bort fra denne e-posten. Koden gir ingen
        tilgang så lenge den ikke blir brukt, og ingen andre får vite at du fikk den.

        Rekfar — turdagboka di
        """;

    public static string Html(string code) => $"""
        <!DOCTYPE html>
        <html lang="nb">
          <body style="font-family: system-ui, sans-serif; color: #1a1a1a;">
            <p>Innloggingskoden din til Rekfar er:</p>
            <p style="font-size: 32px; font-weight: 700; letter-spacing: 6px;">{code}</p>
            <p>Koden er gyldig i omtrent ti minutter og kan bare brukes én gang.</p>
            <p>
              Ba du ikke om å logge inn? Da kan du se bort fra denne e-posten. Koden gir ingen
              tilgang så lenge den ikke blir brukt, og ingen andre får vite at du fikk den.
            </p>
            <p style="color: #666;">Rekfar — turdagboka di</p>
          </body>
        </html>
        """;
}
