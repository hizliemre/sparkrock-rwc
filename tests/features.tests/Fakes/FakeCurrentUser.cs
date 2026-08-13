using domain.Security;

namespace features.tests.Fakes;

/// <summary>
///     Test identity. Defaults to a **non-admin with no schools**, which is the whole point.
/// </summary>
/// <remarks>
///     The production stub returns <c>IsSystemAdmin = true</c>, so every authorisation check passes
///     trivially in development. If test doubles copied that default, a handler that forgot
///     <c>WhereAuthorized</c> entirely would pass every test — the restricting branch would never
///     execute. Opting into admin has to be explicit.
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
