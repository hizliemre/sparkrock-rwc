using domain.Abstraction;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace features.tests.Model;

/// <summary>
///     Asserts DEC-20's partition holds: an entity is soft-deletable, or it is not, and every
///     mechanism that depends on that agrees about which.
/// </summary>
/// <remarks>
///     <para>
///         DEC-20 split <c>BaseEntity</c> from <see cref="SoftDeletableEntity" /> so the two
///         lifecycles are distinguishable by type rather than by convention. The split is only worth
///         anything if the derived mechanisms follow it — the reflective query filter in
///         <c>OnModelCreating</c>, the soft-delete columns, and the <c>is_deleted</c> term in the
///         partial index filters. Each is applied in a different place, and nothing but this file
///         checks that they apply to the same set.
///     </para>
///     <para>
///         The failure this catches is quiet in both directions. An entity that gains a filter it
///         should not have disappears from queries the moment a row is flagged. An entity that loses
///         one returns deleted rows into every projection. Neither throws.
///     </para>
///     <para>
///         Every assertion sweeps <c>GetEntityTypes()</c> rather than a list, because a list is the
///         thing that goes stale — a new entity would be added to the model and not to the list, and
///         the suite would stay green while covering less.
///     </para>
/// </remarks>
public sealed class LifecyclePartitionTests
{
    private static readonly IModel Model = ModelFactory.Create();

    private static IEntityType[] Roots() =>
        Model.GetEntityTypes().Where(e => !e.IsOwned()).ToArray();

    private static bool IsSoftDeletable(IEntityType entityType) =>
        typeof(SoftDeletableEntity).IsAssignableFrom(entityType.ClrType);

    public static TheoryData<string> AllRootEntities()
    {
        TheoryData<string> data = [];
        foreach (IEntityType entityType in Roots())
            data.Add(entityType.ClrType.FullName!);

        return data;
    }

    private static IEntityType Find(string fullName) =>
        Roots().Single(e => e.ClrType.FullName == fullName);

    /// <summary>Guards every theory below against running over an empty model.</summary>
    [Fact]
    public void Model_ContainsBothLifecycleBuckets()
    {
        IEntityType[] roots = Roots();

        Assert.Contains(roots, IsSoftDeletable);
        Assert.Contains(roots, e => !IsSoftDeletable(e));
    }

    [Theory]
    [MemberData(nameof(AllRootEntities))]
    public void Model_EveryEntityDerivesFromBaseEntity(string fullName)
    {
        IEntityType entityType = Find(fullName);

        Assert.True(
            typeof(BaseEntity).IsAssignableFrom(entityType.ClrType),
            $"{entityType.ClrType.Name} is mapped but does not derive from BaseEntity, so the audit "
            + "interceptor will never stamp it and its rows carry no provenance.");
    }

    /// <summary>
    ///     A query filter is present exactly when the entity is soft-deletable.
    /// </summary>
    /// <remarks>
    ///     This is the assertion the reflective loop in <c>OnModelCreating</c> can silently get wrong,
    ///     and the one that catches an entity being moved between buckets by a one-word edit to its
    ///     base class.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllRootEntities))]
    public void Model_QueryFilterPresenceMatchesSoftDeletableBucket(string fullName)
    {
        IEntityType entityType = Find(fullName);
        bool hasFilter = entityType.GetQueryFilter() is not null;

        if (IsSoftDeletable(entityType))
            Assert.True(
                hasFilter,
                $"{entityType.ClrType.Name} is soft-deletable but has no query filter, so deleted rows "
                + "are returned by every query that touches it.");
        else
            Assert.False(
                hasFilter,
                $"{entityType.ClrType.Name} is not soft-deletable but carries a query filter. It has no "
                + "IsDeleted column to filter on, and the filter will either fail to build or exclude "
                + "rows on a predicate nothing maintains.");
    }

    [Theory]
    [MemberData(nameof(AllRootEntities))]
    public void Model_OnlySoftDeletableEntitiesHaveSoftDeleteColumns(string fullName)
    {
        IEntityType entityType = Find(fullName);
        string[] softDeleteProperties = ["IsDeleted", "DeletedAt", "DeletedBy"];

        foreach (string name in softDeleteProperties)
        {
            bool present = entityType.FindProperty(name) is not null;

            Assert.True(
                present == IsSoftDeletable(entityType),
                $"{entityType.ClrType.Name} {(present ? "has" : "lacks")} a {name} column but is "
                + $"{(IsSoftDeletable(entityType) ? "" : "not ")}soft-deletable. The column set and the "
                + "base class must agree, or the interceptor writes to a column the model does not "
                + "declare — or leaves one nothing ever sets.");
        }
    }

    /// <summary>
    ///     No index on a non-soft-deletable entity conditions its filter on <c>is_deleted</c>.
    /// </summary>
    /// <remarks>
    ///     The alert episode index needs <c>is_deleted = false</c> in its filter — without it a
    ///     soft-deleted open episode occupies the unique slot forever while being invisible to every
    ///     query. Copied onto an entity that has no such column, the same clause produces DDL naming
    ///     a column that does not exist, and the migration fails at apply time rather than at build.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllRootEntities))]
    public void Model_OnlySoftDeletableEntitiesHaveIsDeletedIndexFilters(string fullName)
    {
        IEntityType entityType = Find(fullName);

        if (IsSoftDeletable(entityType))
            return;

        foreach (IIndex index in entityType.GetIndexes())
        {
            string? filter = index.GetFilter();

            Assert.False(
                filter?.Contains("is_deleted", StringComparison.OrdinalIgnoreCase) == true,
                $"Index '{index.GetDatabaseName()}' on {entityType.ClrType.Name} filters on is_deleted, "
                + "but the entity is not soft-deletable and has no such column.");
        }
    }

    /// <summary>
    ///     No index filter names a column by its CLR property name.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The naming convention rewrites columns, keys and index names but copies an index filter
    ///         through verbatim, so a predicate written as <c>"IsDeleted = false"</c> produces DDL
    ///         referencing a column that does not exist — and it fails at apply time, not at build.
    ///     </para>
    ///     <para>
    ///         Checked against the property names themselves rather than by scanning for capital
    ///         letters. A filter legitimately contains upper-case SQL keywords (<c>IS NULL</c>,
    ///         <c>AND</c>), so a blanket "no capitals" rule reports every correct filter in the schema
    ///         as broken — which is how this assertion was first written, and it failed on the two
    ///         filters that are right.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Model_NoIndexFilterNamesAColumnByItsClrPropertyName()
    {
        foreach (IEntityType entityType in Roots())
        {
            string[] mismatchedNames = entityType.GetProperties()
                .Where(p => !string.Equals(p.Name, p.GetColumnName(), StringComparison.Ordinal))
                .Select(p => p.Name)
                .ToArray();

            foreach (IIndex index in entityType.GetIndexes())
            {
                string? filter = index.GetFilter();
                if (string.IsNullOrEmpty(filter))
                    continue;

                foreach (string clrName in mismatchedNames)
                    Assert.False(
                        filter.Contains(clrName, StringComparison.Ordinal),
                        $"Index '{index.GetDatabaseName()}' on {entityType.ClrType.Name} filters on "
                        + $"'{clrName}', which is the CLR property name. The naming convention does not "
                        + $"rewrite filters, so the DDL will name a column that does not exist — the "
                        + $"column is '{entityType.GetProperties().Single(p => p.Name == clrName).GetColumnName()}'.");
            }
        }
    }
}
