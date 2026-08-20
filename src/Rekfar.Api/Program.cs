// Rekfar API — the single business-logic boundary for every Rekfar client (ADR-0004).
//
// Skeleton: /v1 routing, OpenAPI, ProblemDetails, CORS, rate limiting and EF Core arrive
// with the API host step. What is here is enough to build, run and probe.

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

// Liveness only, and deliberately without a database check. The database runs on the
// Azure SQL free offer, which is serverless and auto-pauses when idle: a probe that
// touched it would either keep waking it or report unhealthy for the tens of seconds a
// resume takes (ADR-0010; database repo, docs/operations.md).
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();
