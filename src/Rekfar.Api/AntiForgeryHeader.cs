namespace Rekfar.Api;

/// <summary>
/// Requires a header on every state-changing request.
/// </summary>
/// <remarks>
/// <para>
/// The session is a cookie, and the client is not same-site with the API, so the cookie is
/// <c>SameSite=None</c> (ADR-0017). The browser will therefore attach it to a cross-site form
/// post from anywhere — which is the whole of CSRF.
/// </para>
/// <para>
/// A required custom header is the answer, and it is one line rather than a token store: a
/// header a page did not get for free makes the request non-simple, so the browser asks for a
/// CORS preflight first, and the preflight is answered against the origin allowlist the host
/// already keeps. A form post from an unlisted origin never gets to send the request at all.
/// Its value is irrelevant — being able to set it is the proof.
/// </para>
/// <para>
/// Decided here rather than per module, for the reason the rate limit and the origin list are:
/// every write under <c>/v1</c> needs it, and a module that forgets would not fail visibly.
/// </para>
/// </remarks>
internal static class AntiForgeryHeader
{
    /// <summary>Project-specific, so nothing else can be sending it by accident.</summary>
    public const string DefaultName = "X-Rekfar-Csrf";

    public static TBuilder RequireAntiForgeryHeader<TBuilder>(
        this TBuilder builder,
        string headerName)
        where TBuilder : IEndpointConventionBuilder => builder.AddEndpointFilter(async (context, next) =>
        {
            var request = context.HttpContext.Request;

            // Safe methods by RFC 9110: they change nothing, so forging one achieves nothing.
            // OPTIONS is here because a preflight is answered by the CORS middleware long
            // before this, and TRACE because it is not routed at all.
            if (HttpMethods.IsGet(request.Method)
                || HttpMethods.IsHead(request.Method)
                || HttpMethods.IsOptions(request.Method)
                || HttpMethods.IsTrace(request.Method))
            {
                return await next(context);
            }

            if (!request.Headers.ContainsKey(headerName))
            {
                return TypedResults.Problem(
                    title: "Missing anti-forgery header",
                    detail: $"State-changing requests must carry the '{headerName}' header. Its "
                        + "value is not checked; sending it at all is what a cross-site form "
                        + "post cannot do.",
                    statusCode: StatusCodes.Status403Forbidden);
            }

            return await next(context);
        });
}
