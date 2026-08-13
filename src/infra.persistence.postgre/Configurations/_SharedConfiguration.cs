using domain.Abstraction;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace infra.persistence.postgre.Configurations;

internal static class SharedConfiguration
{
    public static void Configure<T>(EntityTypeBuilder<T> builder) where T : class, IAuditableEntity
    {
        builder.Property(m => m.CreatedAt).IsRequired();
        builder.Property(m => m.CreatedBy).IsRequired();
        builder.Property(m => m.ModifiedAt).IsRequired(false);
        builder.Property(m => m.ModifiedBy).IsRequired(false);
        builder.Property(m => m.IsDeleted).IsRequired();
        builder.Property(m => m.DeletedBy).IsRequired(false);
        builder.Property(m => m.DeletedAt).IsRequired(false);
    }
}
