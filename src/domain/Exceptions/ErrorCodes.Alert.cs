namespace domain.Exceptions;

/// <summary>
///     Codes for the <c>ALERT</c> area.
/// </summary>
/// <remarks>
///     A file rather than a line in a shared one (conventions §5), so twelve workstreams adding codes
///     never meet in the same place.
/// </remarks>
public static partial class ErrorCodes
{
    public static class Alert
    {
        /// <summary>
        ///     An open episode already exists for this student, alert type, school year and school.
        /// </summary>
        /// <remarks>
        ///     DEC-18 makes one row one episode, and the partial unique index
        ///     <c>ix_student_alerts_open_episode</c> is what makes a double-raise impossible rather
        ///     than merely unlikely. This code is what the client sees when that index fires and the
        ///     retry did not absorb it.
        /// </remarks>
        public const string DuplicateOpenEpisode = "ALERT.DUPLICATE_OPEN_EPISODE";

        /// <summary>
        ///     The addressed alert does not exist, is soft-deleted, or belongs to a student whose
        ///     current school is outside the caller's scope.
        /// </summary>
        /// <remarks>
        ///     One code for all three, deliberately. Conventions §2's existence-oracle rule requires a
        ///     cross-tenant 404 and a genuine not-found 404 to be indistinguishable, and
        ///     <see cref="NotFoundException" /> carries no message, so the payloads are identical by
        ///     construction rather than by call-site discipline.
        ///     <para>
        ///         It is also the code the school-scoped list route raises when the school in the path
        ///         is outside scope: an alert list for a school the caller may not see must not report
        ///         a different failure from an alert list for a school that does not exist.
        ///     </para>
        /// </remarks>
        public const string NotFound = "ALERT.NOT_FOUND";

        /// <summary>
        ///     The alert already carries a resolution.
        /// </summary>
        /// <remarks>
        ///     409 rather than 200-and-ignore. DEC-18 makes a manual resolution permanently suppress
        ///     re-raising for that student, type, school year and school, so silently overwriting the
        ///     first resolver's identity and reason would discard the only audit record of a decision
        ///     that keeps a safeguarding signal switched off for the rest of the year. It is also why
        ///     the route is <c>POST</c> and not <c>PUT</c>: answering 409 to a verb that promises
        ///     idempotent replacement contradicts the verb (O-02).
        /// </remarks>
        public const string AlreadyResolved = "ALERT.ALREADY_RESOLVED";
    }
}
