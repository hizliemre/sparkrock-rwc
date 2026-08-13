namespace infra.persistence.postgre.ErrorTranslation;

/// <summary>
///     What a named database constraint means to the caller when it is violated.
/// </summary>
/// <param name="ErrorCode">The <c>AREA.CONDITION</c> code the client sees (conventions §5).</param>
/// <param name="Message">The message carried on the translated exception.</param>
/// <param name="Retryable">
///     <see langword="true" /> only when re-running the same work can succeed — a lost update on a
///     row another writer has since committed. A duplicate the caller supplied, a missing foreign
///     key or a failed check will fail identically forever, and marking one of those retryable burns
///     DEC-14's whole attempt bound before returning the same error.
/// </param>
public sealed record ConstraintErrorMapping(string ErrorCode, string Message, bool Retryable);
