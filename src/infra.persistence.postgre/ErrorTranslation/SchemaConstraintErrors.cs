using domain.Exceptions;

namespace infra.persistence.postgre.ErrorTranslation;

/// <summary>
///     Every named constraint in the schema that a caller can provoke, and what it means to them.
/// </summary>
/// <remarks>
///     <para>
///         F01a ships <see cref="ConstraintErrorRegistry.Empty" /> and design §5 makes the feature
///         that authors a constraint add its row. Until this table existed the registry resolved
///         nothing, so every unique violation escaped as a raw <c>DbUpdateException</c> — a 500 with
///         a Postgres message in it.
///     </para>
///     <para>
///         <b>The keys are exact strings and nothing checks them at compile time.</b> A key that
///         matches no constraint is not an error: <c>TryResolve</c> simply misses, the translator
///         returns <see langword="null" />, and the provider exception is rethrown unchanged. That is
///         the same silent-miss shape as VC-36 (a 64-character index name truncated by Postgres, with
///         <c>ConstraintName</c> reporting the truncated form), and it already bit this feature once —
///         two names in F01d's specification table were the pre-rename spellings. <see cref="Names" />
///         exists so a model test can assert every key here names a real index, which is the only
///         mechanism that makes the miss visible.
///     </para>
///     <para>
///         Only constraints a caller can actually reach are listed. Foreign keys are absent by
///         design: they are all <c>Restrict</c>, a violation means a bug rather than a race, and a
///         mapped 409 would present it as the caller's problem.
///     </para>
/// </remarks>
public static class SchemaConstraintErrors
{
    /// <summary>The index name behind each mapping, exposed so a model test can verify it exists.</summary>
    public static class Names
    {
        public const string AttendanceStudentDate = "ix_student_attendances_student_id_attend_date";
        public const string AttendanceLegacyId = "ix_student_attendances_legacy_id";
        public const string SummaryStudentYear = "ix_summaries_student_id_school_year_start";
        public const string AlertOpenEpisode = "ix_student_alerts_open_episode";
        public const string SubmissionIdempotencyKey = "ix_submission_logs_school_id_idempotency_key";
        public const string AttendanceCodeValue = "ix_attendance_codes_value";
    }

    /// <summary>The mappings, keyed by the exact <c>HasDatabaseName</c> string.</summary>
    public static IReadOnlyDictionary<string, ConstraintErrorMapping> Mappings { get; } =
        new Dictionary<string, ConstraintErrorMapping>(StringComparer.Ordinal)
        {
            // Retryable. Two submitters reached the same (student, date) at once; one lost. Re-reading
            // and re-applying is exactly the work that succeeds, and this is the race the legacy
            // procedure resolved by silently discarding one of the two writes (L-12).
            [Names.AttendanceStudentDate] = new(
                ErrorCodes.Attendance.ConcurrentSubmission,
                "Attendance for this student and date was recorded by another submission.",
                Retryable: true),

            // Retryable for the same reason, one table up: the first writer to reach a student's
            // summary for a school year creates it, and the second collides. On retry the row exists
            // and the update path applies.
            [Names.SummaryStudentYear] = new(
                ErrorCodes.Attendance.ConcurrentSubmission,
                "The attendance summary for this student and school year was updated concurrently.",
                Retryable: true),

            // Retryable, and the reasoning is worth stating because it is not obvious. Retrying does
            // not re-raise the alert — it re-reads, observes the episode another writer opened, and
            // declines to raise. The work that succeeds is the submission the raise was part of. Marked
            // non-retryable this would fail an entire attendance submission because an unrelated writer
            // happened to raise the same alert first, which is a worse outcome than a second attempt.
            [Names.AlertOpenEpisode] = new(
                ErrorCodes.Alert.DuplicateOpenEpisode,
                "An open alert already exists for this student, type and school year.",
                Retryable: true),

            // Not retryable. The key is client-supplied, so every attempt produces the same collision
            // and a retry burns DEC-14's whole bound to return the same error. It also means something
            // different: "you already sent this", not "try again".
            [Names.SubmissionIdempotencyKey] = new(
                ErrorCodes.Attendance.DuplicateSubmission,
                "This submission has already been received.",
                Retryable: false),

            // Not retryable, and the index is the only race-free authority for the rule: F03's POST
            // deliberately does no pre-SELECT, because a read-then-insert is a TOCTOU with a nicer
            // stack trace. Unlike every other unique index here this one is *unfiltered*, so the 409
            // fires whether or not the occupying code is active — deactivating never frees a value
            // (F01c §6), and the only route back is PUT { isActive: true } on the existing row.
            //
            // Conventions §5 has carried this row since F01a and F01c shipped both the index and the
            // error code, but nothing mapped one to the other; F03's spec and tasks both state the row
            // already existed. It did not, and nothing failed, because TryResolve is an ordinal lookup
            // where a missing key is a miss rather than an error.
            [Names.AttendanceCodeValue] = new(
                ErrorCodes.AttendanceCode.DuplicateValue,
                "An attendance code with this value already exists.",
                Retryable: false),

            // Not retryable. The importer derives the key from the source row, so a second attempt
            // collides identically.
            [Names.AttendanceLegacyId] = new(
                ErrorCodes.Import.DuplicateLegacyId,
                "A record with this legacy identifier has already been imported.",
                Retryable: false),
        };
}
