// Rekfar API — the single business-logic boundary for every Rekfar client (ADR-0004).
//
// This file is the composition root. It owns cross-cutting concerns — versioning, error
// shape, CORS, rate limiting, health — and lets each module map its own endpoints.
// Business logic belongs in a module, never here.

using System.Diagnostics;
using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Rekfar.Api;
using Rekfar.Catalogue;

var builder = WebApplication.CreateBuilder(args);

// --- Errors ------------------------------------------------------------------------
// RFC 9457 ProblemDetails for every failure, so a client parses one error shape whatever
// went wrong.
builder.Services.AddProblemDetails(options =>
{
    // A correlation id on every error: it is what ties "it failed around 14:03" to the
    // log entry that says why.
    options.CustomizeProblemDetails = context =>
        context.ProblemDetails.Extensions["traceId"] =
            Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
});

// --- OpenAPI -----------------------------------------------------------------------
// The document is generated from the endpoints (ADR-0010 T2) and is what a future native
// client's API client is generated from.
builder.Services.AddOpenApi();

// --- CORS --------------------------------------------------------------------------
// The client is deployed to Netlify and the API to Azure Container Apps, so every browser
// call is cross-origin. ADR-0010 accepted this as the cost of two deploy targets.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

if (allowedOrigins.Length == 0)
{
    // Fail at startup rather than in someone's browser console. A missing origin list
    // breaks the client with a CORS error that says nothing about what is actually wrong,
    // and it breaks it in the browser only — every curl and integration test still passes.
    throw new InvalidOperationException(
        $"No Cors:AllowedOrigins are configured for the '{builder.Environment.EnvironmentName}' "
        + "environment. The web client cannot call the API without at least one allowed origin.");
}

builder.Services.AddCors(options => options.AddPolicy(CorsPolicies.WebApp, policy => policy
    .WithOrigins(allowedOrigins)
    // The origin list is the control here. Pinning methods as well would mean a CORS
    // failure the first time a module adds a write endpoint, diagnosed in a browser.
    .AllowAnyMethod()
    .AllowAnyHeader()));

// --- Rate limiting -----------------------------------------------------------------
// Applied to /v1 rather than globally, so health probes are never throttled.
var permitLimit = builder.Configuration.GetValue("RateLimiting:PermitLimit", 120);
var window = TimeSpan.FromSeconds(builder.Configuration.GetValue("RateLimiting:WindowSeconds", 60));

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy(RateLimitPolicies.Public, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            // Per caller. Behind an ingress this is only a real client address when
            // forwarded headers are trusted — see Hosting:TrustForwardedHeaders below.
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = window,

                // Reject rather than queue. A map client that is panning wants a fast no,
                // not a slow yes for an extent it has already moved away from.
                QueueLimit = 0,
            }));

    options.OnRejected = (context, _) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        }

        return ValueTask.CompletedTask;
    };
});

// --- Reverse proxy -----------------------------------------------------------------
// X-Forwarded-* is spoofable, and trusting it wrongly would let a caller forge the address
// the rate limiter partitions on. Off unless the deployment actually sits behind an
// ingress that overwrites the header.
var trustForwardedHeaders = builder.Configuration.GetValue("Hosting:TrustForwardedHeaders", false);

if (trustForwardedHeaders)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

        // Container Apps' ingress has no stable address and is not on a network the
        // container recognises, so the defaults would reject its header and leave every
        // request looking like it came from the proxy — one rate-limit partition for the
        // whole internet. Safe only because nothing but the ingress can reach the app.
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
}

builder.Services.AddHealthChecks();

// --- Modules -----------------------------------------------------------------------
// Each module registers its own services and maps its own endpoints. The host composes
// them and owns nothing of their internals.
builder.Services.AddCatalogueModule(builder.Configuration);

var app = builder.Build();

// --- Pipeline ----------------------------------------------------------------------
// Order matters. Forwarded headers first, so everything downstream sees the real caller;
// error handling next, so it wraps everything after it.

if (trustForwardedHeaders)
{
    app.UseForwardedHeaders();
}

if (!app.Environment.IsDevelopment())
{
    // In Development the developer exception page is more useful. Integration tests run
    // outside Development, so they assert the same error shape production returns.
    app.UseExceptionHandler();
}

// Turns a bare status code — a 404 from no matching route, a 429 from the limiter — into
// the same ProblemDetails shape as a thrown failure.
app.UseStatusCodePages();

app.UseCors(CorsPolicies.WebApp);
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    // Development only: the schema of every endpoint is a map of the attack surface, and
    // there is no reason to publish it from the running production API. CI generates the
    // document for client generation.
    app.MapOpenApi();
}

// Liveness only, and deliberately without a database check. The database runs on the Azure
// SQL free offer, which is serverless and auto-pauses when idle: a probe that touched it
// would either keep waking it or report unhealthy for the tens of seconds a resume takes
// (ADR-0010; database repo, docs/operations.md).
app.MapHealthChecks("/health");

// Versioned from day one (ADR-0010), so a native client is not broken by web-driven
// changes. Modules map their endpoints into this group, which is also where the public
// rate limit is applied — decided once rather than repeated per endpoint.
var v1 = app.MapGroup("/v1")
    .RequireRateLimiting(RateLimitPolicies.Public)

    // Documented on the group because the limit is a property of the group, not of any one
    // endpoint: every route under /v1 can answer 429.
    .ProducesProblem(StatusCodes.Status429TooManyRequests);

v1.MapCatalogueEndpoints();

app.Run();
