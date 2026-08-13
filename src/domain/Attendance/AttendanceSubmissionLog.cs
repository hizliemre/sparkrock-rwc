using domain.Abstraction;

namespace domain.Attendance;

/// <summary>
///     A record that attendance was submitted for a school and date.
/// </summary>
/// <remarks>
///     Append-only, and the reason the save is a POST rather than a PUT: the identical request twice
///     produces two observable rows, so the operation is not idempotent.
///     <para>
///         Legacy wrote this table and never read it. The read contract is therefore invented rather
///         than migrated.
///     </para>
/// </remarks>
public sealed class AttendanceSubmissionLog : BaseEntity, ISchoolScoped
{
    public required Guid SchoolId { get; set; }

    public required DateOnly AttendDate { get; set; }

    /// <summary>The keyset ordering column, paired with the id because a tick is not unique.</summary>
    public required DateTimeOffset SubmittedAt { get; set; }

    public required int RecordCount { get; set; }

    public required Guid SubmittedBy { get; set; }

    /// <summary>
    ///     Client-supplied retry key, unique per school.
    /// </summary>
    /// <remarks>
    ///     Scoped per school because the key comes from the client: a global index would turn one
    ///     school's retry into another school's conflict. Widening the scope later is free; narrowing
    ///     it can fail on existing data.
    /// </remarks>
    public string? IdempotencyKey { get; set; }
}
