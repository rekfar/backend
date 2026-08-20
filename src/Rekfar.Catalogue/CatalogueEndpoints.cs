using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Rekfar.Catalogue;

/// <summary>The catalogue's HTTP surface. Mapped by the host into its versioned group.</summary>
public static class CatalogueEndpoints
{
    /// <summary>
    /// Enough peaks to draw a useful map at any zoom, small enough to stay quick on a phone
    /// on a slow connection (NFR-PERF-3).
    /// </summary>
    private const int DefaultLimit = 500;

    private const int MaxLimit = 1000;

    /// <summary>
    /// Kartverket's data is CC BY 4.0 (NFR-LEGAL-2). The authoritative per-dataset text
    /// lives in <c>[ref].SourceDataset.Attribution</c>; it is a constant here only because
    /// every peak currently comes from SSR. A second source dataset means reading it.
    /// </summary>
    private const string Attribution = "© Kartverket";

    public static IEndpointRouteBuilder MapCatalogueEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapGet("/peaks", GetPeaks)
            .WithName("GetPeaks")
            .WithSummary("Peaks within a map extent")
            .WithDescription(
                "Returns the peaks inside a bounding box as GeoJSON, highest first. "
                + "'bbox' is west,south,east,north in WGS84 degrees. When the extent holds "
                + "more peaks than 'limit', the highest are returned and 'truncated' is true.")

            // ProblemHttpResult carries no compile-time status code, so the rejection case
            // does not reach the document on its own. Stating it matters: this document is
            // what a native client's API client gets generated from (ADR-0010).
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return builder;
    }

    private static async Task<Results<Ok<PeakFeatureCollection>, ProblemHttpResult>> GetPeaks(
        string? bbox,
        int? minElevationMeters,
        int? limit,
        CatalogueDbContext database,
        CatalogueOptions options,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!BoundingBox.TryParse(bbox, out var extent, out var error))
        {
            return TypedResults.Problem(
                title: "Invalid bbox",
                detail: error,
                statusCode: StatusCodes.Status400BadRequest);
        }

        var take = limit ?? DefaultLimit;

        if (take < 1 || take > MaxLimit)
        {
            return TypedResults.Problem(
                title: "Invalid limit",
                detail: $"'limit' must be between 1 and {MaxLimit}.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var box = extent.ToPolygon();

        var query = database.Peaks.Where(peak => peak.IsActive && box.Intersects(peak.Location));

        if (minElevationMeters is { } minimum)
        {
            // Applied conditionally rather than as `min == null || ...` so the generated SQL
            // has no dead branch for the common case where no minimum was asked for.
            // Peaks with no sampled elevation drop out here, which is correct: the caller
            // asked for peaks known to clear a height.
            query = query.Where(peak => peak.ElevationMeters >= minimum);
        }

        var rows = await query
            // Highest first, so a truncated result is the significant peaks in view rather
            // than an arbitrary subset. SQL Server sorts NULLs last under DESC, which puts
            // not-yet-sampled peaks at the bottom where they belong. Id breaks ties so that
            // paging past this limit later cannot repeat or skip a row.
            .OrderByDescending(peak => peak.ElevationMeters)
            .ThenBy(peak => peak.Id)

            // One more than asked for: if it comes back, the extent held more than we sent.
            .Take(take + 1)
            .Select(peak => new
            {
                peak.Id,
                peak.Name,
                Longitude = peak.Location.X,
                Latitude = peak.Location.Y,
                peak.ElevationMeters,
                peak.ProminenceMeters,
                peak.UtnoUrl,
            })
            .ToListAsync(cancellationToken);

        var truncated = rows.Count > take;

        var features = rows
            .Take(take)
            .Select(row => new PeakFeature
            {
                Id = row.Id,
                Geometry = new PointGeometry { Coordinates = [row.Longitude, row.Latitude] },
                Properties = new PeakProperties
                {
                    Name = row.Name,
                    ElevationMeters = row.ElevationMeters,
                    ProminenceMeters = row.ProminenceMeters,
                    UtnoUrl = row.UtnoUrl,
                },
            })
            .ToArray();

        // Reference data changes only when the Kartverket ingestion job runs, so a map that
        // is being panned should not re-ask the origin for an extent it already has.
        httpContext.Response.Headers.CacheControl = options.CacheSeconds > 0
            ? $"public, max-age={options.CacheSeconds}"
            : "no-store";

        return TypedResults.Ok(new PeakFeatureCollection
        {
            Features = features,
            Attribution = Attribution,
            Truncated = truncated,
        });
    }
}
