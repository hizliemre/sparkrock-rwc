namespace domain.Attendance;

/// <summary>
///     The numbers the save pipeline is built around, in one place (DEC-14, DEC-06).
/// </summary>
/// <remarks>
///     O-42 records that the non-functional numbers in this system lived in prose and were attributed
///     to no constant. <c>PagingRules</c> closed the paging half and its doc comment names this file
///     as the home of the remaining one: "the submission batch cap of 500 stays unsourced and is F07's
///     to name."
///     <para>
///         In <c>domain</c> rather than <c>features</c> because F12's importer applies the same length
///         bounds when it truncates untrusted legacy text (DEC-17), and a second copy of a bound is
///         how L-10 happened. The back-dating window is <b>not</b> here: it is configuration, and
///         <c>domain</c> takes none.
///     </para>
/// </remarks>
public static class AttendanceSave
{
    /// <summary>
    ///     Initial attempt plus two (DEC-14). Not a tuning knob: there is no retry counter yet (O-40),
    ///     so a larger bound buys latency under contention that nothing would measure.
    /// </summary>
    public const int MaxAttempts = 3;

    /// <summary>
    ///     Largest submission accepted in one request.
    /// </summary>
    /// <remarks>
    ///     Deliberately <b>above</b> the roster page cap of 200, so one roster page is always
    ///     submittable in one request. O-10 read the relationship the wrong way round — a batch cap
    ///     below the page cap would be the defect.
    /// </remarks>
    public const int MaxBatchSize = 500;

    /// <summary>Legacy <c>Notes VARCHAR(500)</c>; Postgres <c>text</c> silently accepts more (DEC-06).</summary>
    public const int MaxNotesLength = 500;

    /// <summary>Legacy <c>AttendCode VARCHAR(5)</c> (DEC-06).</summary>
    public const int MaxAttendCodeLength = 5;

    /// <summary>Matches <c>attendance_submission_logs.idempotency_key varchar(64)</c>.</summary>
    public const int MaxIdempotencyKeyLength = 64;
}
