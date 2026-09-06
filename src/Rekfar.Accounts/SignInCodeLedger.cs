using System.Security.Cryptography;
using System.Text;

namespace Rekfar.Accounts;

/// <summary>What a verification attempt is allowed to do before the code is even checked.</summary>
public enum SignInCodeVerdict
{
    /// <summary>Check the code.</summary>
    Allowed,

    /// <summary>This code has already signed somebody in. It is spent.</summary>
    AlreadyUsed,

    /// <summary>This address has used up its guesses for now.</summary>
    TooManyAttempts,
}

/// <summary>
/// Makes a sign-in code single-use, caps how often one address can ask for one, and caps how
/// often one can be guessed.
/// </summary>
/// <remarks>
/// <para>
/// In memory, deliberately. Identity's email token provider derives the code from the user's
/// security stamp and stores nothing, which is exactly what lets the schema carry no token
/// table (rekfar/database#6). "This code has been spent" therefore has nowhere durable to live
/// that would not reintroduce one — so it lives here, next to the request rate limiter, which
/// already makes the same trade for the same reason.
/// </para>
/// <para>
/// That rests on a constraint the deployment already states: <c>maxReplicas</c> is 1
/// (infra/main.bicep), because the rate limiter is in-process. A second replica would give
/// each its own ledger, and a code could be spent once per replica. Raising the replica count
/// means moving both of these to a shared store — decide it, do not drift into it.
/// </para>
/// <para>
/// Codes are held as a keyed hash under a per-process random key, never as themselves: the job
/// is to recognise a code this process has already seen, not to be able to reproduce one.
/// </para>
/// </remarks>
public sealed class SignInCodeLedger(TimeProvider time, AccountsOptions options)
{
    /// <summary>How often expired entries are swept. Bounded work on a quiet endpoint.</summary>
    private static readonly TimeSpan PruneInterval = TimeSpan.FromMinutes(1);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, AddressState> _addresses = new(StringComparer.Ordinal);
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    private readonly TimeSpan _retention = Longest(
        options.CodeRequestWindow,
        options.VerificationWindow,
        options.UsedCodeMemory);

    private DateTimeOffset _nextPrune;

    /// <summary>
    /// Claims one of the address's allowance of codes. False means the address has had enough
    /// for now — the caller still answers as though it had sent one, because saying otherwise
    /// would tell an unauthenticated stranger something about an inbox that is not theirs.
    /// </summary>
    public bool TryClaimCodeRequest(string normalisedEmail)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            var state = Open(normalisedEmail, now);

            if (state.CodeRequestWindowEnds <= now)
            {
                state.CodeRequests = 0;
                state.CodeRequestWindowEnds = now + options.CodeRequestWindow;
            }

            if (state.CodeRequests >= options.MaxCodeRequestsPerAddress)
            {
                return false;
            }

            state.CodeRequests++;
            return true;
        }
    }

    /// <summary>
    /// Counts a verification attempt against the address and says whether the code is worth
    /// checking. An attempt is spent even when the answer is <see cref="SignInCodeVerdict.AlreadyUsed"/>:
    /// replaying a code somebody read over your shoulder should cost the same as guessing one.
    /// </summary>
    public SignInCodeVerdict BeginVerification(string normalisedEmail, string code)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            var state = Open(normalisedEmail, now);

            if (state.VerificationWindowEnds <= now)
            {
                state.Attempts = 0;
                state.VerificationWindowEnds = now + options.VerificationWindow;
            }

            if (state.Attempts >= options.MaxVerificationAttempts)
            {
                return SignInCodeVerdict.TooManyAttempts;
            }

            state.Attempts++;

            return state.UsedCodes.TryGetValue(Fingerprint(normalisedEmail, code), out var until)
                && until > now
                    ? SignInCodeVerdict.AlreadyUsed
                    : SignInCodeVerdict.Allowed;
        }
    }

    /// <summary>
    /// Spends the code. Called once verification has actually succeeded, which is also what
    /// clears the attempt count: someone who has just proved they read the inbox is not the
    /// caller the cap exists for.
    /// </summary>
    public void MarkUsed(string normalisedEmail, string code)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            var state = Open(normalisedEmail, now);

            state.UsedCodes[Fingerprint(normalisedEmail, code)] = now + options.UsedCodeMemory;
            state.Attempts = 0;
        }
    }

    /// <summary>How long the caller must wait before verification will look at a code again.</summary>
    public TimeSpan RetryAfter(string normalisedEmail)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();

            if (!_addresses.TryGetValue(normalisedEmail, out var state))
            {
                return TimeSpan.Zero;
            }

            var remaining = state.VerificationWindowEnds - now;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    private AddressState Open(string normalisedEmail, DateTimeOffset now)
    {
        Prune(now);

        if (!_addresses.TryGetValue(normalisedEmail, out var state))
        {
            state = new AddressState();
            _addresses[normalisedEmail] = state;
        }

        // Every entry is disposable. Touching it on each use means an address nobody has
        // touched for a full retention period falls out, and the dictionary stays the size of
        // recent traffic rather than of all traffic.
        state.ExpiresAt = now + _retention;
        return state;
    }

    private void Prune(DateTimeOffset now)
    {
        if (now < _nextPrune)
        {
            return;
        }

        _nextPrune = now + PruneInterval;

        foreach (var expired in _addresses.Where(entry => entry.Value.ExpiresAt <= now).ToArray())
        {
            _addresses.Remove(expired.Key);
        }
    }

    private string Fingerprint(string normalisedEmail, string code) => Convert.ToHexString(
        HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{normalisedEmail}\n{code}")));

    private static TimeSpan Longest(TimeSpan first, TimeSpan second, TimeSpan third) =>
        first > second
            ? (first > third ? first : third)
            : (second > third ? second : third);

    private sealed class AddressState
    {
        public int CodeRequests { get; set; }

        public DateTimeOffset CodeRequestWindowEnds { get; set; }

        public int Attempts { get; set; }

        public DateTimeOffset VerificationWindowEnds { get; set; }

        public DateTimeOffset ExpiresAt { get; set; }

        public Dictionary<string, DateTimeOffset> UsedCodes { get; } = new(StringComparer.Ordinal);
    }
}
