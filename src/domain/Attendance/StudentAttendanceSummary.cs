using domain.Abstraction;
using domain.ValueObjects;

namespace domain.Attendance;

/// <summary>
///     A student's absence total for a school year.
/// </summary>
/// <remarks>
///     Keyed on student and school year, not school: absences follow the student across a transfer
///     within the year. <see cref="SchoolId" /> is the school of record at the last recalculation and
///     exists for filtering, not identity.
///     <para>
///         Derives from <see cref="BaseEntity" />, so it carries no soft-delete columns and its
///         unique index needs no filter — a soft-deleted summary would otherwise be invisible forever
///         while still occupying the slot its replacement needs.
///     </para>
/// </remarks>
public sealed class StudentAttendanceSummary : BaseEntity, ISchoolScoped
{
    public required Guid StudentId { get; set; }

    /// <summary>School of record at the last recalculation. Not part of the key.</summary>
    public required Guid SchoolId { get; set; }

    public required SchoolYear SchoolYearStart { get; set; }

    public required int TotalAbsences { get; set; }
}
