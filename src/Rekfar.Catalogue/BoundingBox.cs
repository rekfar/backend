using System.Globalization;
using NetTopologySuite;
using NetTopologySuite.Geometries;

namespace Rekfar.Catalogue;

/// <summary>
/// A map extent in WGS84 degrees, as the map client sends it.
/// </summary>
/// <remarks>
/// The wire format is <c>west,south,east,north</c> — the GeoJSON and OGC ordering, and
/// exactly what Leaflet's <c>bounds.toBBoxString()</c> produces, so the client passes its
/// viewport through without assembling anything.
/// </remarks>
public readonly record struct BoundingBox(double West, double South, double East, double North)
{
    /// <summary>
    /// Shared factory fixed to EPSG:4326. A geometry built without the SRID reaches SQL
    /// Server as SRID 0 and fails against a 4326 column rather than silently matching
    /// nothing, but there is no reason to rely on noticing that.
    /// </summary>
    private static readonly GeometryFactory Wgs84 =
        NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

    /// <summary>
    /// Parses the <c>bbox</c> query parameter, returning a caller-facing reason on failure.
    /// </summary>
    public static bool TryParse(string? value, out BoundingBox box, out string? error)
    {
        box = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "The 'bbox' query parameter is required, as 'west,south,east,north' in "
                + "WGS84 degrees.";
            return false;
        }

        var parts = value.Split(',');

        if (parts.Length != 4)
        {
            error = $"'bbox' must have exactly four comma-separated values "
                + $"(west,south,east,north); got {parts.Length}.";
            return false;
        }

        Span<double> values = stackalloc double[4];

        for (var i = 0; i < parts.Length; i++)
        {
            // InvariantCulture is not a default worth inheriting here: the application's
            // first locale is nb-NO, whose decimal separator is the comma that already
            // separates these four values. Parsing under the ambient culture would read
            // "8.31" as 831 wherever the server happened to be configured for Norwegian.
            if (!double.TryParse(
                    parts[i].AsSpan().Trim(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out values[i])
                // NumberStyles.Float accepts "NaN" and "Infinity" under InvariantCulture,
                // and both would sail through the range checks below as false.
                || !double.IsFinite(values[i]))
            {
                error = $"'bbox' value {i + 1} ('{parts[i].Trim()}') is not a number.";
                return false;
            }
        }

        var candidate = new BoundingBox(values[0], values[1], values[2], values[3]);

        if (candidate.West is < -180 or > 180 || candidate.East is < -180 or > 180)
        {
            error = "'bbox' longitudes must be between -180 and 180.";
            return false;
        }

        if (candidate.South is < -90 or > 90 || candidate.North is < -90 or > 90)
        {
            error = "'bbox' latitudes must be between -90 and 90.";
            return false;
        }

        // Strictly ordered, so a degenerate box with no area is rejected rather than
        // returning an empty result the caller has to interpret. Norway is nowhere near the
        // antimeridian, so a box that wraps it is a mistake, not a case to support.
        if (candidate.West >= candidate.East)
        {
            error = "'bbox' west must be less than east.";
            return false;
        }

        if (candidate.South >= candidate.North)
        {
            error = "'bbox' south must be less than north.";
            return false;
        }

        box = candidate;
        error = null;
        return true;
    }

    /// <summary>
    /// The extent as a polygon SQL Server <c>geography</c> will accept.
    /// </summary>
    /// <remarks>
    /// Wound counter-clockwise on purpose. <c>geography</c> follows the left-hand rule, so
    /// the ring's direction decides which side of it is "inside": reversed, this polygon
    /// describes the entire planet except the map extent, and the query returns every peak
    /// the user is not looking at. Nothing about the result's shape reveals the mistake,
    /// which is why an inside/outside pair is asserted in the tests.
    /// </remarks>
    public Polygon ToPolygon() => Wgs84.CreatePolygon(
    [
        new Coordinate(West, South),
        new Coordinate(East, South),
        new Coordinate(East, North),
        new Coordinate(West, North),
        new Coordinate(West, South),
    ]);
}
