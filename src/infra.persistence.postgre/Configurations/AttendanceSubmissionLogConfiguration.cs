using domain.Attendance;
using domain.Schools;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace infra.persistence.postgre.Configurations;

internal sealed class AttendanceSubmissionLogConfiguration : IEntityTypeConfiguration<AttendanceSubmissionLog>
{
    public void Configure(EntityTypeBuilder<AttendanceSubmissionLog> builder)
    {
        builder.ToTable("attendance_submission_logs", table => table.HasCheckConstraint(
            "ck_submission_logs_record_count_not_negative", "record_count >= 0"));

        builder.HasKey(e => e.Id);

        SharedConfiguration.Configure(builder);

        builder.Property(e => e.AttendDate).IsRequired();
        builder.Property(e => e.SubmittedAt).IsRequired();
        builder.Property(e => e.RecordCount).IsRequired();
        builder.Property(e => e.SubmittedBy).IsRequired();
        builder.Property(e => e.IdempotencyKey).IsRequired(false).HasMaxLength(64);

        builder.HasOne<School>().WithMany().HasForeignKey(e => e.SchoolId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_submission_logs_schools_school_id");

        // Keyset pagination: (submitted_at, id) because a TimeProvider tick is not unique, so a
        // cursor on the timestamp alone either skips rows or repeats them. Ascending -- with
        // school_id an equality predicate Postgres scans backwards for a DESC order just as well.
        builder.HasIndex(e => new { e.SchoolId, e.SubmittedAt, e.Id })
            .HasDatabaseName("ix_submission_logs_school_id_submitted_at_id");

        // Per school, not global: the key is client-supplied, so a global index would turn one
        // school's retry into another school's conflict.
        builder.HasIndex(e => new { e.SchoolId, e.IdempotencyKey })
            .IsUnique()
            .HasFilter("idempotency_key IS NOT NULL")
            .HasDatabaseName("ix_submission_logs_school_id_idempotency_key");
    }
}
