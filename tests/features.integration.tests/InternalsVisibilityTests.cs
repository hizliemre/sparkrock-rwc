using features.TestEntities;
using infra.persistence.postgre;

namespace features.integration.tests;

/// <summary>
///     Guards the two <c>InternalsVisibleTo</c> grants this project needs.
/// </summary>
/// <remarks>
///     The grants are what make the tier possible at all (VC-33), and each is needed for a different
///     reason. <c>infra.persistence.postgre</c> is required by this project's own code — the DbContext
///     and the audit interceptor are <c>internal sealed</c>. <c>features</c> is required by every
///     downstream consumer: F03, F04, F07, F08 and F10 all assert against handlers, and handlers are
///     <c>internal</c> by the slice convention.
///     <para>
///         Without this file the second grant is dead weight until the first downstream feature trips
///         over its absence — at merge time, since F01f is <em>blocks-merge</em> for F07. Naming both
///         internal types here turns a removed grant into a compile error today.
///     </para>
/// </remarks>
public sealed class InternalsVisibilityTests
{
    [Fact]
    public void InternalsVisibleTo_GrantsAccessToTheDbContextAndToHandlers()
    {
        Type dbContext = typeof(SparkrockRwcDbContext);
        Type handler = typeof(CreateTestEntity.CommandHandler);

        // Both are genuinely internal: if either were made public the grant would stop being
        // load-bearing and this guard would quietly stop guarding anything.
        Assert.False(dbContext.IsPublic, $"{dbContext.Name} is public; the postgre grant no longer proves anything.");
        Assert.False(handler.IsNestedPublic, $"{handler.Name} is public; the features grant no longer proves anything.");
    }
}
