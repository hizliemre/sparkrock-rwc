using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace domain.Exceptions;

/// <summary>
///     A retryable unique-constraint violation, translated out of the provider's exception.
/// </summary>
/// <remarks>
///     Deliberately **not** derived from <see cref="ConflictException" />. The save pipeline's retry
///     predicate has to tell retryable from permanent, and a <c>catch (ConflictException)</c> that
///     also caught this would swallow the retry and lose the submission.
///     <para>
///         <see cref="Entries" /> is carried rather than discarded because it is the only route from
///         the features layer to the recovery a retry needs: the provider exception type is not
///         referenceable there, and without the entries a failed attempt leaves the change tracker
///         holding stale originals, so every subsequent attempt fails identically and nothing is
///         written.
///     </para>
/// </remarks>
public sealed class ConcurrencyConflictException(
    string constraintName,
    string errorCode,
    string message,
    DbUpdateException innerException)
    : Exception(message, innerException)
{
    public string ConstraintName { get; } = constraintName;

    public string ErrorCode { get; } = errorCode;

    /// <summary>
    ///     The entries the failed save was attempting, for reload-or-detach recovery before a retry.
    /// </summary>
    public IReadOnlyList<EntityEntry> Entries { get; } = innerException.Entries;
}
