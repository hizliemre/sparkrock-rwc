using domain.Security;

namespace features.integration.tests.Fakes;

/// <summary>
///     Test identity for the integration tier. Defaults to a <b>non-admin with no schools</b>.
/// </summary>
/// <remarks>
///     A deliberate copy of <c>features.tests.Fakes.FakeCurrentUser</c>. That one is
///     <c>internal sealed</c> — conventions §6 requires test doubles to be — so it is invisible from
///     this assembly. Making it public would weaken the rule for every future double, and a shared
///     test-support project would couple the two tiers together for twenty-seven lines. Keep the two
///     in step; if a third double ever needs sharing, extract then.
///     <para>
///         The default matters more here than in the in-memory tier, not less. The production stub
///         returns <c>IsSystemAdmin = true</c>; a double copying that would let a handler that forgot
///         <c>WhereAuthorized</c> pass against a real database, which cheerfully returns the
///         unscoped rows. Opting into admin has to be explicit.
///     </para>
/// </remarks>
internal sealed class FakeCurrentUser : ICurrentUser
{
    public Guid UserId { get; init; } = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public string DisplayName { get; init; } = "Fake User";

    public IReadOnlyCollection<Guid> AuthorizedSchoolIds { get; init; } = [];

    public bool IsSystemAdmin { get; init; }

    public static FakeCurrentUser SystemAdmin() => new() { IsSystemAdmin = true };

    public static FakeCurrentUser ScopedTo(params Guid[] schoolIds) => new() { AuthorizedSchoolIds = schoolIds };
}
