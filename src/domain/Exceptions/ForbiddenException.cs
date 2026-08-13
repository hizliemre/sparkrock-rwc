namespace domain.Exceptions;

/// <summary>
///     The resource is visible to the caller but the operation requires a privilege they lack.
/// </summary>
/// <remarks>
///     Distinct from <see cref="NotFoundException" />, and the distinction is the point. Tenancy
///     failures return 404 because a 403 would confirm the record exists. Privilege failures on a
///     globally visible resource are different: an attendance code is readable by everyone, so
///     returning 404 on a deactivate attempt would contradict the 200 on the same id and hide
///     nothing.
/// </remarks>
public sealed class ForbiddenException(string errorCode, string message) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
}
