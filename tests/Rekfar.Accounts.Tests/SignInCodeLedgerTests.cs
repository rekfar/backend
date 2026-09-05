using Rekfar.Accounts;

namespace Rekfar.Accounts.Tests;

public class SignInCodeLedgerTests
{
    private const string Kari = "kari@example.no";
    private const string Ola = "ola@example.no";
    private const string Code = "123456";

    private static readonly AccountsOptions Options = new()
    {
        MaxCodeRequestsPerAddress = 3,
        CodeRequestWindow = TimeSpan.FromMinutes(15),
        MaxVerificationAttempts = 3,
        VerificationWindow = TimeSpan.FromMinutes(15),
        UsedCodeMemory = TimeSpan.FromMinutes(15),
    };

    private static (SignInCodeLedger Ledger, TestClock Clock) Build()
    {
        var clock = new TestClock();
        return (new SignInCodeLedger(clock, Options), clock);
    }

    [Fact]
    public void Allows_an_address_its_allowance_of_codes_and_no_more()
    {
        var (ledger, _) = Build();

        Assert.True(ledger.TryClaimCodeRequest(Kari));
        Assert.True(ledger.TryClaimCodeRequest(Kari));
        Assert.True(ledger.TryClaimCodeRequest(Kari));
        Assert.False(ledger.TryClaimCodeRequest(Kari));
    }

    [Fact]
    public void Gives_the_address_its_allowance_back_when_the_window_passes()
    {
        var (ledger, clock) = Build();

        for (var i = 0; i < Options.MaxCodeRequestsPerAddress; i++)
        {
            Assert.True(ledger.TryClaimCodeRequest(Kari));
        }

        Assert.False(ledger.TryClaimCodeRequest(Kari));

        clock.Advance(Options.CodeRequestWindow + TimeSpan.FromSeconds(1));

        Assert.True(ledger.TryClaimCodeRequest(Kari));
    }

    /// <summary>
    /// The limit is per inbox. One person exhausting theirs must not lock anybody else out —
    /// that would turn a rate limit into a denial of service against every user at once.
    /// </summary>
    [Fact]
    public void Budgets_each_address_separately()
    {
        var (ledger, _) = Build();

        for (var i = 0; i < Options.MaxCodeRequestsPerAddress; i++)
        {
            ledger.TryClaimCodeRequest(Kari);
        }

        Assert.False(ledger.TryClaimCodeRequest(Kari));
        Assert.True(ledger.TryClaimCodeRequest(Ola));
    }

    [Fact]
    public void Allows_the_capped_number_of_guesses_then_stops_answering()
    {
        var (ledger, _) = Build();

        for (var i = 0; i < Options.MaxVerificationAttempts; i++)
        {
            Assert.Equal(SignInCodeVerdict.Allowed, ledger.BeginVerification(Kari, "000000"));
        }

        Assert.Equal(
            SignInCodeVerdict.TooManyAttempts,
            ledger.BeginVerification(Kari, "000000"));
    }

    /// <summary>
    /// Reaching the cap is what invalidates the outstanding code (ADR-0017): the right code
    /// stops being looked at, not just the wrong ones.
    /// </summary>
    [Fact]
    public void Stops_answering_for_the_real_code_too_once_the_cap_is_reached()
    {
        var (ledger, _) = Build();

        for (var i = 0; i < Options.MaxVerificationAttempts; i++)
        {
            ledger.BeginVerification(Kari, "000000");
        }

        Assert.Equal(SignInCodeVerdict.TooManyAttempts, ledger.BeginVerification(Kari, Code));
    }

    [Fact]
    public void A_code_that_has_signed_somebody_in_cannot_be_used_again()
    {
        var (ledger, _) = Build();

        Assert.Equal(SignInCodeVerdict.Allowed, ledger.BeginVerification(Kari, Code));
        ledger.MarkUsed(Kari, Code);

        Assert.Equal(SignInCodeVerdict.AlreadyUsed, ledger.BeginVerification(Kari, Code));
    }

    /// <summary>
    /// The ledger is keyed by address as well as code. Two people cannot be handed the same
    /// six digits by chance and lock each other out.
    /// </summary>
    [Fact]
    public void Spending_a_code_does_not_spend_the_same_digits_for_another_address()
    {
        var (ledger, _) = Build();

        ledger.MarkUsed(Kari, Code);

        Assert.Equal(SignInCodeVerdict.Allowed, ledger.BeginVerification(Ola, Code));
    }

    /// <summary>
    /// Someone who has just proved they read the inbox is not the caller the cap exists for,
    /// and a legitimate sign-in should not leave them one guess from being locked out.
    /// </summary>
    [Fact]
    public void A_successful_sign_in_clears_the_attempt_count()
    {
        var (ledger, _) = Build();

        ledger.BeginVerification(Kari, "000000");
        ledger.BeginVerification(Kari, "111111");
        ledger.BeginVerification(Kari, Code);
        ledger.MarkUsed(Kari, Code);

        for (var i = 0; i < Options.MaxVerificationAttempts; i++)
        {
            Assert.NotEqual(
                SignInCodeVerdict.TooManyAttempts,
                ledger.BeginVerification(Kari, "222222"));
        }
    }

    /// <summary>
    /// A spent code is only remembered for as long as Identity could still accept it. Beyond
    /// that the memory is dead weight, and the digits are free to come round again.
    /// </summary>
    [Fact]
    public void Forgets_a_spent_code_once_it_could_no_longer_be_accepted()
    {
        var (ledger, clock) = Build();

        ledger.MarkUsed(Kari, Code);
        Assert.Equal(SignInCodeVerdict.AlreadyUsed, ledger.BeginVerification(Kari, Code));

        clock.Advance(Options.UsedCodeMemory + TimeSpan.FromSeconds(1));

        Assert.Equal(SignInCodeVerdict.Allowed, ledger.BeginVerification(Kari, Code));
    }

    [Fact]
    public void Reports_how_long_a_blocked_address_must_wait()
    {
        var (ledger, clock) = Build();

        for (var i = 0; i <= Options.MaxVerificationAttempts; i++)
        {
            ledger.BeginVerification(Kari, "000000");
        }

        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(TimeSpan.FromMinutes(10), ledger.RetryAfter(Kari));
    }

    [Fact]
    public void Reports_no_wait_for_an_address_it_has_never_seen()
    {
        var (ledger, _) = Build();

        Assert.Equal(TimeSpan.Zero, ledger.RetryAfter(Ola));
    }
}
