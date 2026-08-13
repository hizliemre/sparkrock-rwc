using domain.Abstraction;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace infra.persistence.postgre.Configurations;

/// <summary>
///     Column mapping shared by every entity configuration.
/// </summary>
/// <remarks>
///     Constrained to the base <em>classes</em>, not the interfaces. Bound to
///     <c>IAuditableEntity</c>, the property lambdas resolve to interface members — and once those are
///     implemented explicitly, EF cannot map the expression and the model fails to build. The failure
///     is loud but not obvious, and every future configuration copies this call.
/// </remarks>
internal static class SharedConfiguration
{
    public static void Configure<T>(EntityTypeBuilder<T> builder) where T : BaseEntity
    {
        builder.Property(m => m.CreatedAt).IsRequired();
        builder.Property(m => m.CreatedBy).IsRequired();
        builder.Property(m => m.ModifiedAt).IsRequired(false);
        builder.Property(m => m.ModifiedBy).IsRequired(false);
    }

    public static void ConfigureSoftDelete<T>(EntityTypeBuilder<T> builder) where T : SoftDeletableEntity
    {
        builder.Property(m => m.IsDeleted).IsRequired();
        builder.Property(m => m.DeletedBy).IsRequired(false);
        builder.Property(m => m.DeletedAt).IsRequired(false);
    }
}
