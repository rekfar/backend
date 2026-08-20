using Microsoft.EntityFrameworkCore;

namespace Rekfar.Catalogue;

/// <summary>
/// The Catalogue module's slice of the database.
/// </summary>
/// <remarks>
/// One context per module rather than one shared across the monolith. Each module maps only
/// the tables it reads, so the seam between modules stays visible in the type system instead
/// of living in a convention nobody enforces.
///
/// There are no migrations here. The schema is defined declaratively in the database
/// repository and deployed from a dacpac; a migration would be a second, competing
/// definition of the same tables.
/// </remarks>
public sealed class CatalogueDbContext(DbContextOptions<CatalogueDbContext> options)
    : DbContext(options)
{
    public DbSet<Peak> Peaks => Set<Peak>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyConfigurationsFromAssembly(typeof(CatalogueDbContext).Assembly);

    // Reference data is written by the Kartverket ingestion job, never by the API. Refusing
    // here turns a mistake into an explanatory error at the call site, rather than a NOT NULL
    // violation naming a column the entity does not even model.
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
        => throw ReadOnly();

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
        => throw ReadOnly();

    private static InvalidOperationException ReadOnly() => new(
        "The catalogue is read-only. [ref] tables are maintained by the Kartverket ingestion "
        + "job, and the entities in this module map only a subset of their columns.");
}
