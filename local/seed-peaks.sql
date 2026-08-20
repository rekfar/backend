/*
    A handful of peaks for local development, so the map has something to draw without
    running the full Kartverket import.

    THIS IS NOT REFERENCE DATA. The real catalogue comes from the ingestion job in the
    database repository (src/Rekfar.Ingest.Peaks), which is the only thing allowed to
    decide what a peak is. These rows exist so that `dotnet run` plus a local database
    produces a visible map, and so the endpoint's edge cases can be exercised by hand.

    Every row is stamped with a 'dev-' ExternalId. That keeps them from ever colliding
    with a real SSR stedsnummer, and makes them removable in one statement:

        DELETE FROM [ref].Peak WHERE ExternalId LIKE 'dev-%';

    Coordinates and elevations are approximate — good enough to put a marker on the right
    mountain, not good enough to quote. Real values arrive with ingestion.

    Idempotent, so it can be run repeatedly: matched on (SourceDatasetId, ExternalId),
    the same natural key a refresh matches on.
*/

-- sqlcmd is an ODBC client and connects with QUOTED_IDENTIFIER OFF, unlike SqlPackage and
-- every application driver. SQL Server then refuses any DML against a table with a spatial
-- index or a constraint calling a spatial method -- which [ref].Peak has both of -- with
-- error 1934. Set here rather than relying on sqlcmd's -I, so the file is correct however
-- it is invoked, matching tests/smoke.sql in the database repository.
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @ssr smallint = (SELECT Id FROM [ref].SourceDataset WHERE Code = 'ssr');
DECLARE @dtm smallint = (SELECT Id FROM [ref].SourceDataset WHERE Code = 'hoydedata');
DECLARE @now datetime2(3) = SYSUTCDATETIME();

IF @ssr IS NULL OR @dtm IS NULL
BEGIN
    THROW 51000, 'ref.SourceDataset is not seeded. Publish the dacpac from the database repository first.', 1;
END

/*
    geography::Point takes LATITUDE FIRST, then longitude — the opposite order to GeoJSON,
    which the API emits as [longitude, latitude]. Getting this backwards is silent: the
    row inserts, the SRID check passes, and the peak simply appears somewhere else. The
    inserted latitudes below are all around 61-79, and the longitudes around 7-18, so a
    swap puts every one of them outside Norway's bounds and is caught by the assertion at
    the end of this script.
*/
;WITH seed (ExternalId, [Name], NavneobjektType, Latitude, Longitude, ElevationMeters, ProminenceMeters, IsActive) AS
(
    SELECT * FROM (VALUES
        -- Jotunheimen: the three highest, clustered so a tight bbox returns them together
        -- and the elevation ordering is visible.
        ('dev-galdhopiggen',    N'Galdhøpiggen',            N'fjell', 61.6363,  8.3126, 2469, 2372, 1),
        ('dev-glittertind',     N'Glittertind',             N'fjell', 61.6516,  8.5561, 2452,  399, 1),
        ('dev-skagastolstind',  N'Store Skagastølstind',    N'fjell', 61.4478,  7.8611, 2405,  855, 1),

        -- Dovrefjell: far enough away to fall outside a Jotunheimen extent, which is what
        -- makes it useful — it proves the bbox is filtering rather than the query
        -- returning everything.
        ('dev-snohetta',        N'Snøhetta',                N'fjell', 62.3193,  9.2678, 2286, 1675, 1),

        -- Svalbard, well outside any mainland extent. The client constrains its viewport
        -- to Norway including Svalbard, so this checks the far end of that range.
        ('dev-newtontoppen',    N'Newtontoppen',            N'fjell', 79.0264, 17.4453, 1713, 1713, 1),

        -- No sampled elevation. SSR carries no height, so this is the state every peak is
        -- in before Høydedata is sampled: it must still be returned, and must sort last
        -- under "highest first".
        ('dev-usamplet',        N'Usamplet topp',           N'topp',  61.5000,  8.4000, NULL, NULL, 1),

        -- Retired, and inside the Jotunheimen extent on purpose: it is the row that proves
        -- the IsActive filter works. A retired peak must never reach the map, but must
        -- survive in the table because somebody may have logged it.
        ('dev-retired',         N'Nedlagt topp',            N'topp',  61.6000,  8.4000, 1500,  120, 0)
    ) AS v (ExternalId, [Name], NavneobjektType, Latitude, Longitude, ElevationMeters, ProminenceMeters, IsActive)
)
MERGE INTO [ref].Peak AS target
USING
(
    SELECT
        SourceDatasetId          = @ssr,
        ExternalId               = seed.ExternalId,
        [Name]                   = seed.[Name],
        SearchName               = seed.[Name],
        NavneobjektType          = seed.NavneobjektType,
        [Location]               = geography::Point(seed.Latitude, seed.Longitude, 4326),
        ElevationMeters          = seed.ElevationMeters,
        ElevationSourceDatasetId = CASE WHEN seed.ElevationMeters IS NULL THEN NULL ELSE @dtm END,
        ElevationSampledAt       = CASE WHEN seed.ElevationMeters IS NULL THEN NULL ELSE @now END,
        ProminenceMeters         = seed.ProminenceMeters,
        PeakRuleVersion          = (SELECT MAX([Version]) FROM [ref].PeakRule),
        FetchedAt                = @now,
        IsActive                 = seed.IsActive,
        RetiredAt                = CASE WHEN seed.IsActive = 1 THEN NULL ELSE @now END
    FROM seed
) AS source
ON  target.SourceDatasetId = source.SourceDatasetId
AND target.ExternalId      = source.ExternalId
WHEN MATCHED THEN UPDATE SET
    [Name]                   = source.[Name],
    SearchName               = source.SearchName,
    NavneobjektType          = source.NavneobjektType,
    [Location]               = source.[Location],
    ElevationMeters          = source.ElevationMeters,
    ElevationSourceDatasetId = source.ElevationSourceDatasetId,
    ElevationSampledAt       = source.ElevationSampledAt,
    ProminenceMeters         = source.ProminenceMeters,
    PeakRuleVersion          = source.PeakRuleVersion,
    FetchedAt                = source.FetchedAt,
    IsActive                 = source.IsActive,
    RetiredAt                = source.RetiredAt
WHEN NOT MATCHED BY TARGET THEN
    INSERT (SourceDatasetId, ExternalId, [Name], SearchName, NavneobjektType, [Location],
            ElevationMeters, ElevationSourceDatasetId, ElevationSampledAt, ProminenceMeters,
            PeakRuleVersion, FetchedAt, IsActive, RetiredAt)
    VALUES (source.SourceDatasetId, source.ExternalId, source.[Name], source.SearchName,
            source.NavneobjektType, source.[Location], source.ElevationMeters,
            source.ElevationSourceDatasetId, source.ElevationSampledAt, source.ProminenceMeters,
            source.PeakRuleVersion, source.FetchedAt, source.IsActive, source.RetiredAt);

-- A swapped latitude/longitude is the one mistake this script could make that nothing
-- else would notice, so it is asserted rather than trusted.
IF EXISTS
(
    SELECT 1 FROM [ref].Peak
    WHERE ExternalId LIKE 'dev-%'
      AND ([Location].Lat NOT BETWEEN 57 AND 81 OR [Location].Long NOT BETWEEN 4 AND 32)
)
BEGIN
    THROW 51001, 'A seeded peak lies outside Norway. geography::Point takes latitude first.', 1;
END

SELECT
    [Name],
    ElevationMeters,
    Latitude  = CAST([Location].Lat AS decimal(9, 4)),
    Longitude = CAST([Location].Long AS decimal(9, 4)),
    IsActive
FROM [ref].Peak
WHERE ExternalId LIKE 'dev-%'
ORDER BY ElevationMeters DESC, Id;
