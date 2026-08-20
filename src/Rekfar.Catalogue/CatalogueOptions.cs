namespace Rekfar.Catalogue;

/// <summary>Deployment-tunable settings for the catalogue. Contract limits are constants.</summary>
public sealed record CatalogueOptions
{
    /// <summary>
    /// How long a client may reuse a map-extent response. Reference data changes only when
    /// the Kartverket ingestion job runs, so this is bounded by the refresh cadence rather
    /// than by anything the API does. Zero disables caching, which is what local development
    /// wants when the seed data is being changed underneath it.
    /// </summary>
    public int CacheSeconds { get; init; } = 3600;
}
