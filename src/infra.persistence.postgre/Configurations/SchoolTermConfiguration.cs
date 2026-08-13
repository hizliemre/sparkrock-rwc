using domain.Schools;
using domain.SchoolTerms;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace infra.persistence.postgre.Configurations;

internal sealed class SchoolTermConfiguration : IEntityTypeConfiguration<SchoolTerm>
{
    public void Configure(EntityTypeBuilder<SchoolTerm> builder)
    {
        builder.ToTable("school_terms", table => table.HasCheckConstraint(
            "ck_school_terms_end_date_not_before_start_date",
            "end_date >= start_date"));

        builder.HasKey(e => e.Id);

        SharedConfiguration.Configure(builder);
        SharedConfiguration.ConfigureLegacy(builder, "school_terms");

        builder.Property(e => e.Name).IsRequired().HasMaxLength(100);
        builder.Property(e => e.StartDate).IsRequired();
        builder.Property(e => e.EndDate).IsRequired();
        builder.Property(e => e.IsActive).IsRequired();

        builder.HasOne<School>()
            .WithMany()
            .HasForeignKey(e => e.SchoolId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_school_terms_schools_school_id");

        // Supports both the overlap probe and term resolution by date. The rejection itself is the
        // write handler's job: an exclusion constraint would need btree_gist, and its 23P01 has no
        // row in the error mapping.
        builder.HasIndex(e => new { e.SchoolId, e.StartDate, e.EndDate })
            .HasDatabaseName("ix_school_terms_school_id_start_date_end_date");
    }
}
