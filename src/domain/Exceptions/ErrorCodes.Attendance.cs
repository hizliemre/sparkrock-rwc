namespace domain.Exceptions;

/// <summary>
///     Codes for the <c>ATTENDANCE</c> area.
/// </summary>
/// <remarks>
///     A file rather than a line in a shared one (conventions §5), so twelve workstreams adding codes
///     never meet in the same place.
/// </remarks>
public static partial class ErrorCodes
{
    public static class Attendance
    {
        /// <summary>
        ///     Two writers reached the same <c>(student, date)</c> row, or the same summary row, at
        ///     once.
        /// </summary>
        /// <remarks>
        ///     Reported only after DEC-14's attempt bound is exhausted. A single occurrence is
        ///     retried and the caller never sees it, which is the point: the legacy procedure had no
        ///     equivalent and simply lost one of the two writes (L-12).
        /// </remarks>
        public const string ConcurrentSubmission = "ATTENDANCE.CONCURRENT_SUBMISSION";

        /// <summary>
        ///     The idempotency key on this submission has already been used by this school.
        /// </summary>
        /// <remarks>
        ///     Not retryable, and deliberately distinct from
        ///     <see cref="ConcurrentSubmission" />. The key is client-supplied, so the same request
        ///     will collide identically forever — retrying burns the whole attempt bound to return
        ///     the same error. It means "you already sent this", which is a different instruction to
        ///     the caller than "try again".
        /// </remarks>
        public const string DuplicateSubmission = "ATTENDANCE.DUPLICATE_SUBMISSION";
    }
}
