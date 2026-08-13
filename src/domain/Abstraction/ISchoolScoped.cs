namespace domain.Abstraction;

/// <summary>
///     An entity that belongs to exactly one school, and is therefore subject to tenant scoping.
/// </summary>
/// <remarks>
///     Get-only. Entities satisfy this with their existing <c>SchoolId</c> property; nothing writes
///     through the interface. Its purpose is to let the authorisation helper be generic without
///     letting scope be reassigned through it.
/// </remarks>
public interface ISchoolScoped
{
    Guid SchoolId { get; }
}
