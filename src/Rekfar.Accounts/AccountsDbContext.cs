using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Rekfar.Accounts;

/// <summary>
/// The Auth &amp; Account module's slice of the database: the Identity user in <c>auth</c>
/// and the profile in <c>app</c>.
/// </summary>
/// <remarks>
/// One context per module, as the catalogue does. This one spans two schemas because the two
/// rows are one thing — <c>app.[User]</c> shares its primary key with <c>auth.[User]</c> and
/// exists only because of it.
///
/// There are no migrations here either. The schema is declared in the database repository and
/// deployed from a dacpac (ADR-0013); a migration would be a second, competing definition.
/// </remarks>
public sealed class AccountsDbContext(DbContextOptions<AccountsDbContext> options)
    : IdentityUserContext<RekfarUser, Guid>(options)
{
    private const string CreatedAt = nameof(UserProfile.CreatedAt);
    private const string UpdatedAt = nameof(UserProfile.UpdatedAt);

    public DbSet<UserProfile> Profiles => Set<UserProfile>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Identity's side tables have no counterpart in this schema, and the convention is to
        // ignore them rather than invent them (database/docs/conventions.md). They come back
        // when a feature needs them — external logins would be the first.
        //
        // Ignoring the claim table is why this module supplies its own claims principal
        // factory: Identity's default reads claims from it on every sign-in.
        modelBuilder.Ignore<IdentityUserClaim<Guid>>();
        modelBuilder.Ignore<IdentityUserLogin<Guid>>();
        modelBuilder.Ignore<IdentityUserToken<Guid>>();
        modelBuilder.Ignore<IdentityUserPasskey<Guid>>();

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AccountsDbContext).Assembly);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampTimestamps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        StampTimestamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Maintains <c>CreatedAt</c> and <c>UpdatedAt</c> on every entity that has them.
    /// </summary>
    /// <remarks>
    /// The database repository's convention is that these are the application's job and not a
    /// trigger's, because a trigger is invisible to anyone reading the schema. Doing it here
    /// rather than at each call site means no write can forget — including the ones inside
    /// Identity's own store, which knows nothing about these two columns.
    ///
    /// Both columns carry a <c>SYSUTCDATETIME()</c> default, but EF sends every mapped column
    /// on an insert, so an unset <c>CreatedAt</c> would write the year 1 rather than fall back
    /// to it.
    /// </remarks>
    private void StampTimestamps()
    {
        var now = DateTime.UtcNow;

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            if (entry.Metadata.FindProperty(UpdatedAt) is null)
            {
                continue;
            }

            entry.Property(UpdatedAt).CurrentValue = now;

            if (entry.State is EntityState.Added)
            {
                entry.Property(CreatedAt).CurrentValue = now;
            }
        }
    }
}
