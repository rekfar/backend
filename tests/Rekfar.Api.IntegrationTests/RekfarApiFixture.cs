using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SqlServer.Dac;
using Rekfar.Accounts;
using Testcontainers.MsSql;

namespace Rekfar.Api.IntegrationTests;

/// <summary>
/// A real SQL Server with the real schema, and the API on top of it.
/// </summary>
/// <remarks>
/// The schema is published from the database repository's dacpac rather than rebuilt here.
/// That repository owns the schema; a hand-maintained approximation of it in this one would
/// drift, and drift is exactly what these tests exist to catch. It also matters for what is
/// being tested: the spatial index, the SRID check constraint and the geography column
/// itself are the subject, so an approximation would prove nothing about them.
/// </remarks>
public sealed class RekfarApiFixture : IAsyncLifetime
{
    private const string DatabaseName = "Rekfar";

    // Azure SQL cannot change a database's collation after creation, so local and CI
    // databases are created with the one production has. Sorting and comparison would
    // otherwise differ from production in exactly the place Norwegian names live.
    private const string Collation = "Norwegian_100_CI_AS";

    /// <summary>
    /// The header the host requires on every state-changing request. Its value is never
    /// checked; being able to send it at all is the point (see AntiForgeryHeader).
    /// </summary>
    private const string AntiForgeryHeader = "X-Rekfar-Csrf";

    private MsSqlContainer _container = null!;
    private WebApplicationFactory<Program> _factory = null!;

    public HttpClient Client { get; private set; } = null!;

    /// <summary>
    /// The sign-in codes the API tried to send. The tests read a code the way its owner does —
    /// out of the message the module decided to send — rather than by asking Identity to
    /// generate one again, so what is asserted is the whole path from request to inbox.
    /// </summary>
    public CapturingSignInCodeSender Emails { get; } = new();

    /// <summary>
    /// The database behind the API, for the few assertions that are about rows rather than
    /// responses — that Identity really did land in <c>auth.[User]</c> and the profile in
    /// <c>app.[User]</c>, sharing one key. Mapping onto a schema this repository does not own
    /// is exactly the thing worth checking against the schema itself.
    /// </summary>
    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // Microsoft publishes no arm64 image. On Apple Silicon this runs translated, which
        // is slower to start but behaves identically — including the spatial types these
        // tests depend on. Pinned to the same tag the database repository's CI uses.
        _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

        await _container.StartAsync();

        var master = _container.GetConnectionString();

        await ExecuteAsync(master, $"CREATE DATABASE [{DatabaseName}] COLLATE {Collation};");

        PublishSchema(master);

        var connectionString = new SqlConnectionStringBuilder(master)
        {
            InitialCatalog = DatabaseName,
        }.ConnectionString;

        ConnectionString = connectionString;

        await SeedAsync(connectionString);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // Not Development: the developer exception page would replace the ProblemDetails
            // these tests assert, and production's error shape is the one worth testing.
            builder.UseEnvironment("Testing");

            // Both modules read the same connection string name, so this one setting covers
            // the catalogue's context and the account module's.
            builder.UseSetting($"ConnectionStrings:{Catalogue.CatalogueModule.ConnectionStringName}", connectionString);
            builder.UseSetting("Cors:AllowedOrigins:0", "https://rekfar.test");

            // Pinned so the cache assertion is about the header the endpoint sets rather
            // than about whatever the default happens to be.
            builder.UseSetting("Catalogue:CacheSeconds", "600");

            // The limiter has its own tests; here it would only make the suite flaky.
            builder.UseSetting("RateLimiting:PermitLimit", "10000");

            // Every test shares one caller as far as the limiter can tell — TestServer has no
            // remote address — so the per-caller budget for sign-in codes has to hold the whole
            // suite. The per-address budget is the one these tests actually exercise, and it is
            // left at its real value because each test uses an address of its own.
            builder.UseSetting("Auth:CodeRequestRateLimit", "10000");

            // Zero, so a revoked session is rejected on its next request rather than up to five
            // minutes later. The five minutes are the deployed value and the whole point of the
            // setting; a test cannot wait them out and still be a test.
            builder.UseSetting("Auth:SecurityStampValidationSeconds", "0");

            builder.ConfigureServices(services =>
                services.AddSingleton<ISignInCodeSender>(Emails));
        });

        // Through CreateClient rather than the factory directly, so the one shared client has
        // the https base address and the anti-forgery header too. Nothing using it today needs
        // either — the catalogue is anonymous and read-only — but a client that silently drops
        // the session cookie is not a thing to leave lying about.
        Client = CreateClient();
    }

    /// <summary>
    /// A client with its own cookie jar — one browser, one device.
    /// </summary>
    /// <param name="handleCookies">
    /// False for a client that manages the session cookie by hand, which is how a second device
    /// is simulated.
    /// </param>
    /// <remarks>
    /// The base address is <c>https</c> deliberately. The session cookie is issued
    /// <c>Secure</c> in every environment, and <see cref="System.Net.CookieContainer"/> honours
    /// that: over <c>http</c> it would store the cookie and never send it back, and every
    /// authenticated test would fail for a reason that has nothing to do with the API.
    /// </remarks>
    public HttpClient CreateClient(bool handleCookies = true)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = handleCookies,
        });

        client.DefaultRequestHeaders.Add(AntiForgeryHeader, "1");

        return client;
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();

        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private static void PublishSchema(string masterConnectionString)
    {
        var dacpac = ResolveDacpac();

        using var package = DacPackage.Load(dacpac);

        new DacServices(masterConnectionString).Deploy(
            package,
            DatabaseName,
            upgradeExisting: true,
            options: new DacDeployOptions
            {
                CreateNewDatabase = false,

                // The post-deployment script seeds the source datasets and the peak rule,
                // both of which the seeded peaks reference by foreign key.
                RunDeploymentPlanExecutors = false,
            });
    }

    /// <summary>
    /// Finds the dacpac built by the database repository. It is a build artefact of another
    /// repository, so there is no version of this that does not involve knowing where that
    /// repository is.
    /// </summary>
    private static string ResolveDacpac()
    {
        var configured = Environment.GetEnvironmentVariable("REKFAR_DACPAC");

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured)
                ? configured
                : throw new FileNotFoundException(
                    $"REKFAR_DACPAC points at '{configured}', which does not exist.", configured);
        }

        // Walk up from the test binaries looking for a sibling checkout of the database
        // repository, which is how it is laid out locally.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "database", "src", "Rekfar.Database", "bin", "Release", "Rekfar.Database.dacpac");

            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "Could not find Rekfar.Database.dacpac. Build it in the database repository "
            + "(dotnet build src/Rekfar.Database --configuration Release), or set REKFAR_DACPAC "
            + "to a dacpac downloaded from that repository's CI.");
    }

    /// <summary>
    /// Seven peaks, chosen for what they prove rather than to look like a catalogue. The
    /// extent used by the tests is Jotunheimen: 7.5,61.3,8.8,61.8.
    /// </summary>
    private static async Task SeedAsync(string connectionString)
    {
        const string sql = """
            DECLARE @ssr smallint = (SELECT Id FROM [ref].SourceDataset WHERE Code = 'ssr');
            DECLARE @dtm smallint = (SELECT Id FROM [ref].SourceDataset WHERE Code = 'hoydedata');
            DECLARE @now datetime2(3) = SYSUTCDATETIME();

            IF @ssr IS NULL OR @dtm IS NULL
                THROW 51000, 'ref.SourceDataset is not seeded; the post-deployment script did not run.', 1;

            INSERT INTO [ref].Peak
                (SourceDatasetId, ExternalId, [Name], SearchName, NavneobjektType, [Location],
                 ElevationMeters, ElevationSourceDatasetId, ElevationSampledAt, ProminenceMeters,
                 FetchedAt, IsActive, RetiredAt)
            VALUES
                -- geography::Point takes latitude first. Inside the test extent:
                (@ssr, 'test-galdhopiggen',   N'Galdhøpiggen',         N'Galdhøpiggen',         N'fjell',
                 geography::Point(61.6363,  8.3126, 4326), 2469, @dtm, @now, 2372, @now, 1, NULL),
                (@ssr, 'test-glittertind',    N'Glittertind',          N'Glittertind',          N'fjell',
                 geography::Point(61.6516,  8.5561, 4326), 2452, @dtm, @now,  399, @now, 1, NULL),
                (@ssr, 'test-skagastolstind', N'Store Skagastølstind', N'Store Skagastølstind', N'fjell',
                 geography::Point(61.4478,  7.8611, 4326), 2405, @dtm, @now,  855, @now, 1, NULL),

                -- Inside the extent, but never sampled: must still be returned, and must
                -- sort last under "highest first".
                (@ssr, 'test-usamplet',       N'Usamplet topp',        N'Usamplet topp',        N'topp',
                 geography::Point(61.5000,  8.4000, 4326), NULL, NULL, NULL, NULL, @now, 1, NULL),

                -- Inside the extent and retired: the row that proves the IsActive filter.
                (@ssr, 'test-retired',        N'Nedlagt topp',         N'Nedlagt topp',         N'topp',
                 geography::Point(61.6000,  8.4000, 4326), 1500, @dtm, @now,  120, @now, 0, @now),

                -- Outside the extent. If the extent polygon were wound the wrong way these
                -- two are what would come back, and nothing else would look wrong.
                (@ssr, 'test-snohetta',       N'Snøhetta',             N'Snøhetta',             N'fjell',
                 geography::Point(62.3193,  9.2678, 4326), 2286, @dtm, @now, 1675, @now, 1, NULL),
                (@ssr, 'test-newtontoppen',   N'Newtontoppen',         N'Newtontoppen',         N'fjell',
                 geography::Point(79.0264, 17.4453, 4326), 1713, @dtm, @now, 1713, @now, 1, NULL);
            """;

        await ExecuteAsync(connectionString, sql);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition(nameof(RekfarApiCollection))]
public sealed class RekfarApiCollection : ICollectionFixture<RekfarApiFixture>;
