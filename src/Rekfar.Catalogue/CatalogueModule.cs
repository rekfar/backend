using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Rekfar.Catalogue;

/// <summary>
/// The module's registration surface. The host composes modules; it does not reach inside
/// them, and nothing outside this assembly needs to know the module uses EF Core.
/// </summary>
public static class CatalogueModule
{
    /// <summary>Name of the connection string in configuration.</summary>
    public const string ConnectionStringName = "Rekfar";

    public static IServiceCollection AddCatalogueModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Configuration, not connectivity. A database that is merely unreachable is a
            // runtime condition worth retrying; a connection string that was never set is a
            // deployment mistake, and finding it on the first request rather than at startup
            // only delays the same failure.
            throw new InvalidOperationException(
                $"No '{ConnectionStringName}' connection string is configured. Set it with "
                + $"`dotnet user-secrets set \"ConnectionStrings:{ConnectionStringName}\" \"<value>\"` "
                + "locally, or in the container app's configuration.");
        }

        services.AddSingleton(new CatalogueOptions
        {
            CacheSeconds = configuration.GetValue("Catalogue:CacheSeconds", 3600),
        });

        var commandTimeout = configuration.GetValue("Database:CommandTimeoutSeconds", 60);

        services.AddDbContext<CatalogueDbContext>(options => options
            .UseSqlServer(connectionString, sqlServer =>
            {
                // Geometry travels as NetTopologySuite types, never provider-specific spatial
                // SQL. NTS is the same geometry model Npgsql uses for PostGIS, which is what
                // keeps a later move a provider swap rather than a rewrite (ADR-0010).
                sqlServer.UseNetTopologySuite();

                // The free-offer database is serverless and auto-pauses when idle. Resuming
                // surfaces as a transient failure, so the first query after a quiet period
                // depends on this retrying rather than failing the request.
                sqlServer.EnableRetryOnFailure(
                    maxRetryCount: 6,
                    maxRetryDelay: TimeSpan.FromSeconds(30),
                    errorNumbersToAdd: null);

                // Above the 30-second default for the same reason: a resume can take tens of
                // seconds, and timing out during one would turn a slow first request into a
                // failed one.
                sqlServer.CommandTimeout(commandTimeout);
            })

            // Nothing in this module writes, so tracking every materialised peak would buy
            // change detection no caller uses. Map extents return hundreds of rows at a time.
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));

        return services;
    }
}
