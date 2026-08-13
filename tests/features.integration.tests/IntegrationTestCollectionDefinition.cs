namespace features.integration.tests;

/// <summary>
///     The single collection every integration test joins, so one container serves the whole
///     assembly and the migration runs once.
/// </summary>
/// <remarks>
///     xUnit runs the tests of one collection serially, which is also what makes a shared database
///     safe to reason about. A second collection would mean a second container and a second
///     migration — add one only when a test genuinely needs an isolated database, and say why here.
///     <para>
///         Named <c>…Definition</c> rather than <c>…Collection</c>: CA1711 reserves the
///         <c>Collection</c> suffix for <c>ICollection</c> implementations, and warnings are errors.
///     </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class IntegrationTestCollectionDefinition : ICollectionFixture<PostgresContainerFixture>
{
    public const string Name = "postgres-container";
}
