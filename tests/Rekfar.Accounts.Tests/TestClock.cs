namespace Rekfar.Accounts.Tests;

/// <summary>
/// A clock the test moves by hand.
/// </summary>
/// <remarks>
/// Every rule in <c>SignInCodeLedger</c> is about a window closing, and the only alternative
/// way to test one is to wait for it. Fifteen real minutes per assertion is not a test suite.
/// </remarks>
internal sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public TestClock()
        : this(new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero))
    {
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
