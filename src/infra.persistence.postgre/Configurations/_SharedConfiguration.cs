using domain.Abstraction;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>
    ///     Maps the legacy identity column and its unique filtered index.
    /// </summary>
    /// <remarks>
    ///     The index name is passed in rather than derived, because it is quoted verbatim by the
    ///     constraint-to-error-code mapping and a derived name would drift silently.
    ///     <para>
    ///         The filter is hand-written in snake case. The naming convention rewrites columns,
    ///         indexes and keys, but an index filter is an opaque SQL string it copies verbatim — a
    ///         PascalCase filter produces DDL referring to a column that does not exist.
    ///     </para>
    /// </remarks>
    public static void ConfigureLegacy<T>(EntityTypeBuilder<T> builder, string tableName)
        where T : class, ILegacyEntity
    {
        builder.Property(m => m.LegacyId).IsRequired(false);

        builder.HasIndex(m => m.LegacyId)
            .IsUnique()
            .HasFilter("legacy_id IS NOT NULL")
            .HasDatabaseName($"ix_{tableName}_legacy_id");
    }

    public static void ConfigureSoftDelete<T>(EntityTypeBuilder<T> builder) where T : SoftDeletableEntity
    {
        builder.Property(m => m.IsDeleted).IsRequired();
        builder.Property(m => m.DeletedBy).IsRequired(false);
        builder.Property(m => m.DeletedAt).IsRequired(false);
    }
}
