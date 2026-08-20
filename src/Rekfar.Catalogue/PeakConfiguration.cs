using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Rekfar.Catalogue;

/// <summary>
/// Maps <see cref="Peak"/> onto <c>[ref].Peak</c>.
/// </summary>
/// <remarks>
/// Table, schema and every column are named explicitly rather than left to convention. The
/// schema is owned by the database repository, not by this one, so the mapping is a stated
/// contract with it: renaming a property here cannot silently start reading a different
/// column, and a column renamed there fails loudly instead of quietly binding to nothing.
/// </remarks>
internal sealed class PeakConfiguration : IEntityTypeConfiguration<Peak>
{
    public void Configure(EntityTypeBuilder<Peak> builder)
    {
        builder.ToTable("Peak", "ref");

        builder.HasKey(peak => peak.Id);

        // bigint IDENTITY, per the database's split of ref (bigint) from app/auth (guid).
        builder.Property(peak => peak.Id)
            .HasColumnName("Id")
            .ValueGeneratedOnAdd();

        builder.Property(peak => peak.Name)
            .HasColumnName("Name")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(peak => peak.Location)
            .HasColumnName("Location")
            .HasColumnType("geography")
            .IsRequired();

        builder.Property(peak => peak.ElevationMeters)
            .HasColumnName("ElevationMeters");

        builder.Property(peak => peak.ProminenceMeters)
            .HasColumnName("ProminenceMeters");

        builder.Property(peak => peak.UtnoUrl)
            .HasColumnName("UtnoUrl")
            .HasMaxLength(400);

        builder.Property(peak => peak.IsActive)
            .HasColumnName("IsActive");
    }
}
