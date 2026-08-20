using NetTopologySuite.Geometries;

namespace Rekfar.Catalogue;

/// <summary>
/// A mountain top (fjelltopp), sourced from Kartverket's SSR with elevation sampled from
/// Høydedata.
/// </summary>
/// <remarks>
/// A deliberate subset of <c>[ref].Peak</c>: the columns the catalogue serves, not the
/// columns the table has. Provenance (<c>SourceDatasetId</c>, <c>ExternalId</c>,
/// <c>FetchedAt</c>), the ingestion-owned <c>SearchName</c> and <c>NavneobjektType</c>, and
/// the peak-rule version are ingestion's business and are not mapped here.
///
/// That subsetting is also why this module never writes: an insert would violate NOT NULL
/// constraints on columns this type does not know exist.
/// </remarks>
public sealed class Peak
{
    public long Id { get; init; }

    /// <summary>
    /// Display name. The database collation sorts æ, ø and å where a Norwegian reader
    /// expects them; the accent-insensitive <c>SearchName</c> beside it exists for lookup
    /// and is not mapped, because searching is not what this endpoint does.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// WGS84 (EPSG:4326) and nothing else — a check constraint enforces the SRID, because a
    /// wrong-SRID row is invisible until a spatial query silently returns nothing.
    /// </summary>
    public required Point Location { get; init; }

    /// <summary>
    /// Nullable because it is derived: SSR carries no height, so elevation is sampled from a
    /// DTM and a peak can exist before it has been sampled.
    /// </summary>
    public int? ElevationMeters { get; init; }

    public int? ProminenceMeters { get; init; }

    /// <summary>
    /// Optional outbound link to a human-written description (FR-REF-7). UT.no is a link
    /// target, never a data source, and every view must render without it.
    /// </summary>
    public string? UtnoUrl { get; init; }

    /// <summary>
    /// Retired rows stay in the table, because a peak somebody has logged has to survive an
    /// upstream deletion. Every catalogue query filters on this.
    /// </summary>
    public bool IsActive { get; init; }
}
