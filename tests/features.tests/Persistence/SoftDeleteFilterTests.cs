using domain.Abstraction;
using infra.persistence.postgre;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace features.tests.Persistence;

public sealed class SoftDeleteFilterTests
{
    /// <summary>
    ///     V-11's verification, and the mechanism DEC-20's later total-and-disjoint partition test
    ///     extends once there are entities to partition.
    /// </summary>
    /// <remarks>
    ///     The reflective loop is the single owner of query filters: EF Core 8 allows one filter per
    ///     entity type and silently replaces it on a second call, and the loop runs after
    ///     <c>ApplyConfigurationsFromAssembly</c>, so anything declared in a configuration is
    ///     discarded without a diagnostic. Asserting presence-by-bucket here is what catches a filter
    ///     applied to the wrong set.
    /// </remarks>
    [Fact]
    public void Model_AppliesQueryFilterToSoftDeletableEntitiesOnly()
    {
        using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        foreach (IEntityType entityType in dbContext.Model.GetEntityTypes())
        {
            bool isSoftDeletable = typeof(SoftDeletableEntity).IsAssignableFrom(entityType.ClrType);
            bool hasFilter = entityType.GetQueryFilter() is not null;

            Assert.True(
                isSoftDeletable == hasFilter,
                $"{entityType.ClrType.Name}: soft-deletable={isSoftDeletable} but query filter present={hasFilter}.");
        }
    }
}
