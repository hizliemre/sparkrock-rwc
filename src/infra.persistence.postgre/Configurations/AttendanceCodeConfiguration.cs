using domain.AttendanceCodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace infra.persistence.postgre.Configurations;

internal sealed class AttendanceCodeConfiguration : IEntityTypeConfiguration<AttendanceCode>
{
    public void Configure(EntityTypeBuilder<AttendanceCode> builder)
    {
        builder.ToTable("attendance_codes", table => table.HasCheckConstraint(
            "ck_attendance_codes_value_upper",
            "value = upper(value)"));

        builder.HasKey(e => e.Id);

        SharedConfiguration.Configure(builder);
        SharedConfiguration.ConfigureLegacy(builder, "attendance_codes");

        builder.Property(e => e.Value).IsRequired().HasMaxLength(5);
        builder.Property(e => e.Description).IsRequired().HasMaxLength(100);
        builder.Property(e => e.IsAbsent).IsRequired();
        builder.Property(e => e.IsExcused).IsRequired();
        builder.Property(e => e.IsActive).IsRequired();

        // Unfiltered, unlike every other unique index here. Deactivating a code therefore never
        // frees its value for reuse -- stated as a rule rather than left implied, because a partial
        // index conditioned on a column nobody maintains becomes a duplicate-insertion hole the day
        // it is ever flipped.
        builder.HasIndex(e => e.Value)
            .IsUnique()
            .HasDatabaseName("ix_attendance_codes_value");
    }
}
