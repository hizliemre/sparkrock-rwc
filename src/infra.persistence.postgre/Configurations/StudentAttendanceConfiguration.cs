using domain.Attendance;
using domain.AttendanceCodes;
using domain.Schools;
using domain.SchoolTerms;
using domain.Students;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace infra.persistence.postgre.Configurations;

internal sealed class StudentAttendanceConfiguration : IEntityTypeConfiguration<StudentAttendance>
{
    public void Configure(EntityTypeBuilder<StudentAttendance> builder)
    {
        builder.ToTable("student_attendances", table => table.HasCheckConstraint(
            "ck_student_attendances_minutes_late_not_negative",
            "minutes_late IS NULL OR minutes_late >= 0"));

        builder.HasKey(e => e.Id);

        SharedConfiguration.Configure(builder);
        SharedConfiguration.ConfigureSoftDelete(builder);
        SharedConfiguration.ConfigureLegacy(builder, "student_attendances");

        builder.Property(e => e.AttendDate).IsRequired();
        builder.Property(e => e.AttendCode).IsRequired().HasMaxLength(5);
        builder.Property(e => e.AttendCodeDescription).IsRequired().HasMaxLength(100);
        builder.Property(e => e.IsAbsent).IsRequired();
        builder.Property(e => e.IsExcused).IsRequired();
        builder.Property(e => e.MinutesLate).IsRequired(false);
        builder.Property(e => e.Notes).IsRequired(false).HasMaxLength(500);

        builder.HasOne<Student>().WithMany().HasForeignKey(e => e.StudentId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_student_attendances_students_student_id");
        builder.HasOne<School>().WithMany().HasForeignKey(e => e.SchoolId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_student_attendances_schools_school_id");
        builder.HasOne<SchoolTerm>().WithMany().HasForeignKey(e => e.TermId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_student_attendances_school_terms_term_id");
        builder.HasOne<AttendanceCode>().WithMany().HasForeignKey(e => e.AttendanceCodeId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_student_attendances_attendance_codes_attendance_code_id");
        builder.HasOne<AttendanceSubmissionLog>().WithMany().HasForeignKey(e => e.SubmissionId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_student_attendances_submission_logs_submission_id");

        // One record per student per day. Filtered so a soft-deleted row frees the slot for its
        // correction; the predicate is hand-written in snake case because the naming convention
        // copies an index filter through verbatim.
        builder.HasIndex(e => new { e.StudentId, e.AttendDate })
            .IsUnique()
            .HasFilter("is_deleted = false")
            .HasDatabaseName("ix_student_attendances_student_id_attend_date");

        builder.HasIndex(e => new { e.SchoolId, e.AttendDate })
            .HasDatabaseName("ix_student_attendances_school_id_attend_date");

        // Unfiltered, deliberately. EF skips the automatic foreign-key index because the composite
        // above leads with student_id -- but that one is partial, so anything reading past the query
        // filter (referential-integrity checks on student delete, and the importer) would seq-scan
        // the largest table in the schema.
        builder.HasIndex(e => e.StudentId)
            .HasDatabaseName("ix_student_attendances_student_id");
    }
}
