using domain.Alerts;
using domain.Schools;
using domain.Students;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace infra.persistence.postgre.Configurations;

internal sealed class StudentAlertConfiguration : IEntityTypeConfiguration<StudentAlert>
{
    public void Configure(EntityTypeBuilder<StudentAlert> builder)
    {
        builder.ToTable("student_alerts", table =>
        {
            table.HasCheckConstraint("ck_student_alerts_absence_count_not_negative", "absence_count >= 0");
            table.HasCheckConstraint(
                "ck_student_alerts_school_year_start", "school_year_start BETWEEN 1900 AND 2100");

            // A resolution is all-or-nothing: a source or a reason without a timestamp is a
            // half-written resolution that reads as open.
            table.HasCheckConstraint(
                "ck_student_alerts_resolution_consistent",
                "(resolved_at IS NULL AND resolution_source IS NULL) OR (resolved_at IS NOT NULL AND resolution_source IS NOT NULL)");
        });

        builder.HasKey(e => e.Id);

        SharedConfiguration.Configure(builder);
        SharedConfiguration.ConfigureSoftDelete(builder);

        builder.Property(e => e.AlertType).IsRequired().HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.SchoolYearStart).IsRequired();
        builder.Property(e => e.AbsenceCount).IsRequired();
        builder.Property(e => e.ThresholdAtRaise).IsRequired();
        builder.Property(e => e.ResolvedAt).IsRequired(false);
        builder.Property(e => e.ResolvedBy).IsRequired(false);
        builder.Property(e => e.ResolutionSource).IsRequired(false).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.ResolutionReason).IsRequired(false).HasMaxLength(500);

        builder.HasOne<Student>().WithMany().HasForeignKey(e => e.StudentId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_student_alerts_students_student_id");
        builder.HasOne<School>().WithMany().HasForeignKey(e => e.SchoolId
            ).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_student_alerts_schools_school_id");

        // One open episode per student, type, year and school. The name is short deliberately: the
        // four-column form ran to 67 characters, Postgres truncates at 63, and the provider then
        // reports the truncated name -- so an error-code registry keyed on the declared name would
        // never match and a raw exception would escape instead of a 409.
        //
        // The is_deleted term is not optional. Without it a soft-deleted open episode keeps the slot
        // forever while being invisible to every query.
        builder.HasIndex(e => new { e.StudentId, e.AlertType, e.SchoolYearStart, e.SchoolId })
            .IsUnique()
            .HasFilter("resolved_at IS NULL AND is_deleted = false")
            .HasDatabaseName("ix_student_alerts_open_episode");

        builder.HasIndex(e => new { e.StudentId, e.SchoolYearStart })
            .HasDatabaseName("ix_student_alerts_student_id_school_year_start");

        // The school-wide worklist: "who in this school is flagged this year". Specified by F01d and
        // omitted from the first cut of this configuration -- F10's alert list and F12's
        // reconciliation report are both keyed exactly this way, and without it each of them
        // sequential-scans the alert table.
        //
        // Unfiltered on purpose, unlike the episode index above. A partial index conditioned on
        // is_deleted would be unusable to the importer and to any audit path that reads past the
        // soft-delete filter, and the planner can use the plain form for both.
        builder.HasIndex(e => new { e.SchoolId, e.SchoolYearStart })
            .HasDatabaseName("ix_student_alerts_school_id_school_year_start");
    }
}
