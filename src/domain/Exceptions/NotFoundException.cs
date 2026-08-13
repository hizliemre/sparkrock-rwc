namespace domain.Exceptions;

/// <summary>
///     A resource addressed by the path does not exist, or is outside the caller's scope.
/// </summary>
/// <remarks>
///     Takes no message on purpose. Conventions §2 requires a cross-tenant 404 and a genuine
///     not-found 404 to emit an identical payload, and a type with no message parameter makes that
///     true by construction rather than by every call site remembering. The error code still varies
///     by area; the payload does not.
/// </remarks>
public sealed class NotFoundException(string errorCode) : Exception(NotFoundMessage)
{
    public const string NotFoundMessage = "The requested resource was not found.";

    public string ErrorCode { get; } = errorCode;
}
