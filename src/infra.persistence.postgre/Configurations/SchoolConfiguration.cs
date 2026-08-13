using domain.Schools;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace infra.persistence.postgre.Configurations;

internal sealed class SchoolConfiguration : IEntityTypeConfiguration<School>
{
    public void Configure(EntityTypeBuilder<School> builder)
    {
        builder.ToTable("schools", table => table.HasCheckConstraint(
            "ck_schools_absence_alert_threshold_positive",
            "absence_alert_threshold IS NULL OR absence_alert_threshold >= 1"));

        builder.HasKey(e => e.Id);

        SharedConfiguration.Configure(builder);
        SharedConfiguration.ConfigureLegacy(builder, "schools");

        builder.Property(e => e.Name).IsRequired().HasMaxLength(200);
        builder.Property(e => e.TimeZoneId).IsRequired().HasMaxLength(64);

        // No database default. A DEFAULT 10 here would be a second home for a rule that already has
        // one, which is the duplication this model exists to remove.
        builder.Property(e => e.AbsenceAlertThreshold).IsRequired(false);

        // Also no default: HasDefaultValue(true) makes EF omit an explicit false from the INSERT.
        builder.Property(e => e.IsActive).IsRequired();
    }
}
