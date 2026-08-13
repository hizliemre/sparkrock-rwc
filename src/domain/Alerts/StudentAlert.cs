using domain.Abstraction;
using domain.ValueObjects;

namespace domain.Alerts;

/// <summary>
///     One chronic-absence episode for a student, at a school, in a school year.
/// </summary>
/// <remarks>
///     One row is one episode. Re-raising after a resolution creates a new row, so each cycle keeps
///     its own attribution rather than overwriting the last.
///     <para>
///         Suppression is keyed per school. Keying it school-agnostically produced a safeguarding
///         failure: while a former school's alert was open, the receiving school could neither raise
///         its own nor see or resolve the existing one.
///     </para>
/// </remarks>
public sealed class StudentAlert : SoftDeletableEntity, ISchoolScoped
{
    public required Guid StudentId { get; set; }

    public required Guid SchoolId { get; set; }

    public required AlertType AlertType { get; set; }

    public required SchoolYear SchoolYearStart { get; set; }

    public required int AbsenceCount { get; set; }

    /// <summary>
    ///     The threshold in force when this episode opened. Audit only — every comparison uses the
    ///     school's current threshold, so changing it does not retroactively re-evaluate.
    /// </summary>
    public required int ThresholdAtRaise { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }

    public Guid? ResolvedBy { get; set; }

    public ResolutionSource? ResolutionSource { get; set; }

    /// <summary>Written by the resolving person. Free text, bounded.</summary>
    public string? ResolutionReason { get; set; }
}
