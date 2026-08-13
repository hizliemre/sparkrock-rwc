using domain.Import;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace infra.persistence.postgre.Configurations;

internal sealed class LegacyImportAnomalyConfiguration : IEntityTypeConfiguration<LegacyImportAnomaly>
{
    public void Configure(EntityTypeBuilder<LegacyImportAnomaly> builder)
    {
        builder.ToTable("legacy_import_anomalies");
        builder.HasKey(e => e.Id);

        SharedConfiguration.Configure(builder);

        builder.Property(e => e.Entity).IsRequired().HasMaxLength(64);
        builder.Property(e => e.LegacyId).IsRequired(false);
        builder.Property(e => e.BatchId).IsRequired();
        builder.Property(e => e.AnomalyCode).IsRequired().HasMaxLength(64);
        builder.Property(e => e.Detail).IsRequired(false).HasMaxLength(500);

        // Not unique: one legacy row can fail several ways.
        builder.HasIndex(e => new { e.BatchId, e.AnomalyCode })
            .HasDatabaseName("ix_import_anomalies_batch_id_anomaly_code");
    }
}
