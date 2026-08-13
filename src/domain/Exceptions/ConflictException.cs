namespace domain.Exceptions;

/// <summary>
///     The request is valid but conflicts with persisted state.
/// </summary>
public sealed class ConflictException(string errorCode, string message) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
}
