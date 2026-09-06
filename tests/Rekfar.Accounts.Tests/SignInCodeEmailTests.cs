using Rekfar.Accounts;

namespace Rekfar.Accounts.Tests;

public class SignInCodeEmailTests
{
    private const string Code = "428913";

    [Fact]
    public void Carries_the_code_in_both_bodies()
    {
        Assert.Contains(Code, SignInCodeEmail.PlainText(Code));
        Assert.Contains(Code, SignInCodeEmail.Html(Code));
    }

    /// <summary>
    /// A subject line shows on a lock screen. This is the one message Rekfar sends where being
    /// worth trusting is the entire point, and a code visible without unlocking the phone is
    /// the opposite of that.
    /// </summary>
    [Fact]
    public void Keeps_the_code_out_of_the_subject()
    {
        Assert.DoesNotContain(Code, SignInCodeEmail.Subject);
    }

    /// <summary>
    /// Norwegian UI (ADR-0003), and the reassurance for the person who did not ask to sign in —
    /// which is the message that stops an unexpected code reading as a break-in.
    /// </summary>
    [Fact]
    public void Is_written_in_Norwegian_and_says_what_to_do_if_you_did_not_ask()
    {
        Assert.Contains("Innloggingskoden din til Rekfar", SignInCodeEmail.PlainText(Code));
        Assert.Contains("Ba du ikke om å logge inn?", SignInCodeEmail.PlainText(Code));
        Assert.Contains("Ba du ikke om å logge inn?", SignInCodeEmail.Html(Code));
    }
}
