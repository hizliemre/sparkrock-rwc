using domain.Abstraction;

namespace features.tests.Fakes;

/// <summary>
///     Minimal <see cref="ISchoolScoped" /> stand-in for exercising the authorisation helpers at the
///     unit tier. Translation to <c>school_id = ANY (...)</c> is verified against a real provider by
///     the integration tier, not re-asserted here.
/// </summary>
internal sealed class ScopedRow : ISchoolScoped
{
    public required Guid SchoolId { get; init; }

    public required string Name { get; init; }
}
