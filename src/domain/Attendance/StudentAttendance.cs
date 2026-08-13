using domain.Abstraction;

namespace domain.Attendance;

/// <summary>
///     One student's attendance for one day.
/// </summary>
/// <remarks>
///     Soft-deletable, so a correction can hide a row without losing it, and filtered out of every
///     query by the reflective filter.
///     <para>
///         Carries both <see cref="AttendanceCodeId" /> and a snapshot of the code's meaning. The
///         foreign key gives referential integrity; the snapshot is what makes redefining a code
///         leave history alone. There is deliberately <b>no navigation property</b> to the code — the
///         absence is the structural half of that invariant, because <c>a.AttendanceCode.Description</c>
///         then does not compile.
///     </para>
/// </remarks>
public sealed class StudentAttendance : SoftDeletableEntity, ILegacyEntity, ISchoolScoped
{
    public required Guid StudentId { get; set; }

    public required Guid SchoolId { get; set; }

    public required DateOnly AttendDate { get; set; }

    /// <summary>Null when no term covers the date — preserved from legacy rather than rejected.</summary>
    public Guid? TermId { get; set; }

    public required Guid AttendanceCodeId { get; set; }

    /// <summary>
    ///     The submission that last wrote this row, or null for imported rows.
    /// </summary>
    /// <remarks>
    ///     Answers "which submission last wrote this", not "which submissions touched this". A save is
    ///     a partial upsert, so an updated row's value is overwritten — a submission read therefore
    ///     returns its rows that have not since been superseded.
    /// </remarks>
    public Guid? SubmissionId { get; set; }

    /// <summary>Snapshot of the code as it was at save time. Never recomputed.</summary>
    public required string AttendCode { get; set; }

    /// <summary>Snapshot of the code's description as it was at save time. Never recomputed.</summary>
    public required string AttendCodeDescription { get; set; }

    /// <summary>Snapshot. Never recomputed from the code table.</summary>
    public required bool IsAbsent { get; set; }

    /// <summary>Snapshot. Never recomputed from the code table.</summary>
    public required bool IsExcused { get; set; }

    public int? MinutesLate { get; set; }

    /// <summary>
    ///     Free text. Routinely carries health and safeguarding detail, so it never reaches a log
    ///     line or an error message.
    /// </summary>
    public string? Notes { get; set; }

    public int? LegacyId { get; set; }
}
