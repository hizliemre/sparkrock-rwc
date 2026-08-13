using domain.Schools;
using domain.Students;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace infra.persistence.postgre.Configurations;

internal sealed class StudentConfiguration : IEntityTypeConfiguration<Student>
{
    public void Configure(EntityTypeBuilder<Student> builder)
    {
        builder.ToTable("students");
        builder.HasKey(e => e.Id);

        SharedConfiguration.Configure(builder);
        SharedConfiguration.ConfigureLegacy(builder, "students");

        builder.Property(e => e.FirstName).IsRequired().HasMaxLength(100);
        builder.Property(e => e.LastName).IsRequired().HasMaxLength(100);
        builder.Property(e => e.Grade).IsRequired(false).HasMaxLength(10);
        builder.Property(e => e.IsActive).IsRequired();

        // Restrict, not the framework default. EF defaults a required relationship to Cascade, and
        // at that default removing one school physically deletes every student in it.
        //
        // No navigation property on either side: the query filter emits an INNER JOIN through a
        // navigation, and split-query behaviour is configured globally, so a navigation nobody needs
        // is a performance and correctness liability rather than a convenience.
        builder.HasOne<School>()
            .WithMany()
            .HasForeignKey(e => e.SchoolId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_students_schools_school_id");

        // Leading school_id also serves the foreign key, which Postgres does not index automatically.
        builder.HasIndex(e => new { e.SchoolId, e.IsActive })
            .HasDatabaseName("ix_students_school_id_is_active");
    }
}
