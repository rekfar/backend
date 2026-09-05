using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Rekfar.Accounts;

/// <summary>Maps <see cref="UserProfile"/> onto <c>app.[User]</c>.</summary>
/// <remarks>
/// Table, schema and every column named explicitly, for the same reason the catalogue names
/// them: the schema belongs to another repository, and a column renamed there should fail a
/// build here rather than quietly bind to nothing.
/// </remarks>
internal sealed class UserProfileConfiguration : IEntityTypeConfiguration<UserProfile>
{
    public void Configure(EntityTypeBuilder<UserProfile> builder)
    {
        builder.ToTable("User", "app");

        builder.HasKey(profile => profile.Id);

        // Never generated. The value is auth.[User].Id — FK_app_User_auth_User is what makes
        // that a rule rather than a habit, and it is also the cascade that turns "delete my
        // account" into one statement.
        builder.Property(profile => profile.Id)
            .HasColumnName("Id")
            .ValueGeneratedNever();

        builder.Property(profile => profile.DisplayName)
            .HasColumnName("DisplayName")
            .HasMaxLength(80)
            .IsRequired();

        // varchar, not nvarchar: a BCP-47 tag is ASCII by definition, and the column is
        // varchar(16). Left to convention, EF would send an nvarchar parameter and SQL Server
        // would convert on every comparison.
        builder.Property(profile => profile.Locale)
            .HasColumnName("Locale")
            .HasColumnType("varchar(16)")
            .IsRequired();

        builder.Property(profile => profile.DefaultPrivacy)
            .HasColumnName("DefaultPrivacy")
            .HasColumnType("varchar(16)")
            .IsRequired();

        builder.Property(profile => profile.CreatedAt)
            .HasColumnName("CreatedAt")
            .HasColumnType("datetime2(3)");

        builder.Property(profile => profile.UpdatedAt)
            .HasColumnName("UpdatedAt")
            .HasColumnType("datetime2(3)");
    }
}
