using domain.Attendance;
using domain.Schools;
using domain.Students;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace infra.persistence.postgre.Configurations;

internal sealed class StudentAttendanceSummaryConfiguration : IEntityTypeConfiguration<StudentAttendanceSummary>
{
    public void Configure(EntityTypeBuilder<StudentAttendanceSummary> builder)
    {
        builder.ToTable("student_attendance_summaries", table =>
        {
            table.HasCheckConstraint(
                "ck_student_attendance_summaries_total_absences_not_negative", "total_absences >= 0");
            table.HasCheckConstraint(
                "ck_student_attendance_summaries_school_year_start", "school_year_start BETWEEN 1900 AND 2100");
        });

        builder.HasKey(e => e.Id);

        SharedConfiguration.Configure(builder);

        builder.Property(e => e.SchoolYearStart).IsRequired();
        builder.Property(e => e.TotalAbsences).IsRequired();

        builder.HasOne<Student>().WithMany().HasForeignKey(e => e.StudentId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_summaries_students_student_id");
        builder.HasOne<School>().WithMany().HasForeignKey(e => e.SchoolId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_summaries_schools_school_id");

        // Unfiltered -- there is no is_deleted column here, which is the visible payoff of keeping
        // this entity off the soft-deletable base.
        builder.HasIndex(e => new { e.StudentId, e.SchoolYearStart })
            .IsUnique()
            .HasDatabaseName("ix_summaries_student_id_school_year_start");

        // The optimistic concurrency token, and the single most easily broken line in the schema.
        // It must be a *uint* shadow property: Npgsql maps that to the xmin system column, which the
        // database maintains. Configured on a byte[] it compiles, creates a real bytea column nothing
        // ever writes, and every concurrency check then passes trivially -- a lost update with no
        // error and no failing test.
        builder.Property<uint>("Version").IsRowVersion();
    }
}
