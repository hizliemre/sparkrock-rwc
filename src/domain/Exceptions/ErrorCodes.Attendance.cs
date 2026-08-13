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
        ///     Envelope code for the accumulated block of per-entry reference failures.
        /// </summary>
        /// <remarks>
        ///     The individual items carry <see cref="StudentNotOnRoster" /> and
        ///     <see cref="UnknownCode" />; this is the top-level code the client branches on. It exists
        ///     because conventions §2 decides status by the addressed resource, and a submission whose
        ///     body has problems is one 400 listing all of them rather than a round trip per defect.
        /// </remarks>
        public const string SubmissionRejected = "ATTENDANCE.SUBMISSION_REJECTED";

        /// <summary>
        ///     The student is not on the addressed school's roster.
        /// </summary>
        /// <remarks>
        ///     Covers <em>every</em> reason a student id does not resolve — unknown, another school's,
        ///     or transferred away (V-13). Conventions §2's existence-oracle rule requires the cases to
        ///     be indistinguishable, and it names the code that must not exist:
        ///     <c>ATTENDANCE.STUDENT_NOT_FOUND</c>. There is one rejection branch in the handler, so
        ///     the cases cannot diverge by a later edit.
        /// </remarks>
        public const string StudentNotOnRoster = "ATTENDANCE.STUDENT_NOT_ON_ROSTER";

        /// <summary>
        ///     The attendance code does not exist, or exists and is inactive.
        /// </summary>
        /// <remarks>
        ///     Legacy stored an unrecognised code as present-unexcused and the row was then invisible to
        ///     every read (L-06); the submission is now rejected (V-04 ●). Unknown and inactive fall out
        ///     of the same set difference and produce an identical violation — conventions §2 rules an
        ///     inactive code a 400 field error, superseding V-14's original 409 for the code half.
        /// </remarks>
        public const string UnknownCode = "ATTENDANCE.UNKNOWN_CODE";

        /// <summary>The same student appears more than once in one payload (V-15).</summary>
        public const string DuplicateStudent = "ATTENDANCE.DUPLICATE_STUDENT";

        /// <summary>The submission carries more than <c>AttendanceSave.MaxBatchSize</c> entries.</summary>
        public const string BatchSizeExceeded = "ATTENDANCE.BATCH_SIZE_EXCEEDED";

        /// <summary>
        ///     The submitted date is in the school's future, or older than the back-dating window.
        /// </summary>
        /// <remarks>
        ///     School-local, not UTC (DEC-12): <c>UtcNow.Date</c> rolls the attendance date mid-afternoon
        ///     for many schools. Legacy fixed the date at form load and could not back-date at all
        ///     (L-16, V-25 ●); an unbounded date writes attendance into an arbitrary school year, and
        ///     back-dating is the quiet path to auto-resolving a safeguarding alert.
        /// </remarks>
        public const string DateOutOfRange = "ATTENDANCE.DATE_OUT_OF_RANGE";

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

        /// <summary>
        ///     The addressed submission log does not exist, or belongs to a school outside
        ///     <c>AuthorizedSchoolIds</c>.
        /// </summary>
        /// <remarks>
        ///     One code for both, because conventions §2's existence-oracle rule requires the two to be
        ///     indistinguishable: a distinguishable status or code confirms that a submission with that
        ///     id exists somewhere. <see cref="NotFoundException" /> takes no message, so the payloads
        ///     are identical by construction rather than by call-site discipline.
        ///     <para>
        ///         Also the answer for a school that has simply never submitted anything — no, it is
        ///         not: that is an empty list, not a 404 (F11 spec §5). This code is only ever raised
        ///         for a <em>path</em> id that does not resolve.
        ///     </para>
        /// </remarks>
        public const string SubmissionNotFound = "ATTENDANCE.SUBMISSION_NOT_FOUND";
    }
}
