namespace domain.Exceptions;

/// <summary>
///     Carries per-item failures that were accumulated rather than short-circuited.
/// </summary>
/// <remarks>
///     The save pipeline runs its reference checks unconditionally so one round trip reports every
///     problem; staging them costs a round trip per defect. Status is decided by the addressed
///     resource, never by an item in here — checks on a path resource throw
///     <see cref="NotFoundException" /> or <see cref="ConflictException" /> before accumulation starts.
/// </remarks>
public sealed class BusinessRuleException(string errorCode, IReadOnlyList<Violation> violations)
    : Exception($"{violations.Count} rule violation(s).")
{
    public string ErrorCode { get; } = errorCode;

    public IReadOnlyList<Violation> Violations { get; } = violations;
}
