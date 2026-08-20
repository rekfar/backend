namespace Rekfar.Api;

/// <summary>Named CORS policies. The host owns these; modules never configure CORS.</summary>
internal static class CorsPolicies
{
    /// <summary>The Rekfar web client, and later any other first-party browser client.</summary>
    public const string WebApp = "webapp";
}

/// <summary>Named rate-limiting policies, applied per route group rather than globally.</summary>
internal static class RateLimitPolicies
{
    /// <summary>
    /// Anonymous, unauthenticated reads — the peak catalogue is browsable without an
    /// account (FR-PEAK-5), which makes it the one surface with no cost attached to
    /// calling it. Health probes sit outside /v1 and are deliberately not limited.
    /// </summary>
    public const string Public = "public";
}
