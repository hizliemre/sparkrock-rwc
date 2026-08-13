using System.Linq.Expressions;
using System.Reflection;
using domain;
using domain.Abstraction;
using infra.persistence.sql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace infra.persistence.postgre;

internal sealed class SparkrockRwcDbContext(DbContextOptions<SparkrockRwcDbContext> options) : DbContext(options), IDbContext
{
    public DbSet<TestEntity> TestEntities { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        Type dbContextType = typeof(SparkrockRwcDbContext);
        modelBuilder.ApplyConfigurationsFromAssembly(dbContextType.Assembly);
        
         foreach (IMutableEntityType entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (entityType.IsOwned())
                continue;

            // For TPH inheritance, only apply filter to the root entity type
            IMutableEntityType rootType = entityType.GetRootType();
            if (rootType != entityType)
                continue;

            // soft delete filter - applies only to SoftDeletableEntity types (DEC-20)
            if (!typeof(SoftDeletableEntity).IsAssignableFrom(entityType.ClrType)) continue;
            MethodInfo? softDeleteMethod = typeof(SparkrockRwcDbContext)
                .GetMethod(nameof(GetSoftDeleteFilter), BindingFlags.Public | BindingFlags.Static);

            if (softDeleteMethod == null) continue;
            MethodInfo genericSoftDeleteMethod = softDeleteMethod.MakeGenericMethod(entityType.ClrType);
            object? softDeleteFilter = genericSoftDeleteMethod.Invoke(null, []);

            if (softDeleteFilter != null)
                modelBuilder.Entity(entityType.ClrType)
                    .HasQueryFilter((LambdaExpression)softDeleteFilter);
        }
    }
    
    public static LambdaExpression GetSoftDeleteFilter<TEntity>()
        where TEntity : SoftDeletableEntity
    {
        ParameterExpression entityParam = Expression.Parameter(typeof(TEntity), "e");

        // e.IsDeleted
        MemberExpression isDeletedProperty = Expression.Property(entityParam, nameof(SoftDeletableEntity.IsDeleted));

        // !e.IsDeleted
        UnaryExpression notDeleted = Expression.Not(isDeletedProperty);

        // Build lambda: e => !e.IsDeleted
        Expression<Func<TEntity, bool>> filter = Expression.Lambda<Func<TEntity, bool>>(notDeleted, entityParam);

        return filter;
    }
}