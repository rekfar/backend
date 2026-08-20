namespace Rekfar.Catalogue;

/// <summary>
/// Peaks in a map extent, as a GeoJSON <c>FeatureCollection</c> (RFC 7946).
/// </summary>
/// <remarks>
/// GeoJSON rather than a flat array because both renderers consume it directly — Leaflet
/// through <c>L.geoJSON</c>, MapLibre as a <c>geojson</c> source — so no client transforms
/// the payload before drawing it, and a later trails or cabins layer speaks the same format.
///
/// <c>attribution</c> and <c>truncated</c> are foreign members, which RFC 7946 permits on a
/// FeatureCollection.
/// </remarks>
public sealed record PeakFeatureCollection
{
    public string Type => "FeatureCollection";

    public required IReadOnlyList<PeakFeature> Features { get; init; }

    /// <summary>
    /// Kartverket's data is CC BY 4.0 and the credit is a licence condition, not a courtesy
    /// (NFR-LEGAL-2), so it travels with the data rather than living only in the client.
    /// </summary>
    public required string Attribution { get; init; }

    /// <summary>
    /// True when the extent holds more peaks than the limit returned. The client shows the
    /// most prominent peaks and can invite the user to zoom in, rather than quietly drawing
    /// an arbitrary subset as though it were the whole picture.
    /// </summary>
    public required bool Truncated { get; init; }
}

public sealed record PeakFeature
{
    public string Type => "Feature";

    /// <summary>The catalogue id, at the Feature level where RFC 7946 puts it.</summary>
    public required long Id { get; init; }

    public required PointGeometry Geometry { get; init; }

    public required PeakProperties Properties { get; init; }
}

public sealed record PointGeometry
{
    public string Type => "Point";

    /// <summary>
    /// <c>[longitude, latitude]</c> — GeoJSON's order, which is the reverse of how a
    /// coordinate is usually spoken and written elsewhere in this codebase. Getting it
    /// backwards puts every Norwegian peak in the Indian Ocean.
    /// </summary>
    public required double[] Coordinates { get; init; }
}

public sealed record PeakProperties
{
    public required string Name { get; init; }

    /// <summary>Metres above sea level (moh.). Null until ingestion has sampled a DTM.</summary>
    public int? ElevationMeters { get; init; }

    public int? ProminenceMeters { get; init; }

    /// <summary>Optional outbound description link (FR-REF-7); clients render without it.</summary>
    public string? UtnoUrl { get; init; }
}
