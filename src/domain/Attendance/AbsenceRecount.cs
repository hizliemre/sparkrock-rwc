using domain.ValueObjects;

namespace domain.Attendance;

/// <summary>
///     The absence recount, as a composable query — extracted from the save pipeline rather than
///     inlined in it.
/// </summary>
/// <remarks>
///     design §5's shared-artifact table requires this: F07 counts prior absences to compute a new
///     total, and F12 recomputes every summary from scratch with the same predicate (V-18, because the
///     legacy values were produced by a predicate that filtered nothing — L-12). Two copies of this
///     predicate is L-10 reproduced in the migration that exists to remove it.
///     <para>
///         The table's nominated owner was F01b, whose spec put the recount out of scope; F07 authors
///         it to the contract the table specifies.
///     </para>
/// </remarks>
public static class AbsenceRecount
{
    /// <summary>
    ///     One student's absence count for a school year.
    /// </summary>
    /// <remarks>
    ///     A query projection, not a wire shape — conventions §3's "no positional records" rule governs
    ///     requests and responses, and a constructor projection is the form EF translates most
    ///     predictably.
    /// </remarks>
    public sealed record AbsenceCount(Guid StudentId, int Count);

    /// <summary>
    ///     Absences for the given students in the given school year, <b>excluding</b> one date.
    /// </summary>
    /// <param name="attendances">The attendance set, normally <c>IDbContext.StudentAttendances</c>.</param>
    /// <param name="studentIds">The students to count. Every runtime collection type translates (VC-30).</param>
    /// <param name="schoolYear">The school year, applied as a half-open date range (VC-13, DEC-07).</param>
    /// <param name="excludedDate">
    ///     The date being submitted, excluded from the count.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         <b><paramref name="excludedDate" /> is what makes DEC-14 work.</b> The submission writes
    ///         exactly one date — it is a route segment — so the prior count can be read before any
    ///         write and the new total computed in memory as <c>prior + (isAbsent ? 1 : 0)</c>. That
    ///         replaces the <c>SELECT … FOR UPDATE</c> DEC-04 specified, which EF Core 8 cannot express
    ///         (VC-01, VC-02). Today's row is still committed in the database when this runs: without
    ///         the exclusion a student already marked absent today is counted once here and once again
    ///         by the <c>+1</c>, and a student being corrected from absent to present keeps the old
    ///         absence in their total for the rest of the year.
    ///     </para>
    ///     <para>
    ///         <b>There is deliberately no school predicate, and no <c>WhereAuthorized</c>.</b> V-07c ●
    ///         requires the count to span schools within the school year, because absences follow the
    ///         student across a transfer. VC-13's verified SQL <em>does</em> carry
    ///         <c>s.school_id = @__schoolId_1</c> and the entry is still correct about
    ///         <em>translation</em> — but copying it as the query shape produces a single-school count
    ///         and reinstates D-05's ambiguity. The parameter list has no school in it, which is the
    ///         strongest available guard against one being added;
    ///         <c>AbsenceRecountTests.PriorAbsenceCounts_SpansSchools</c> and
    ///         <c>SaveDailyAttendanceHandlerTests.Handle_WhenStudentHasAbsencesAtAnotherSchoolThisYear_IncludesThemInTheTotal</c>
    ///         are what fail if one appears anyway.
    ///     </para>
    ///     <para>
    ///         <c>NOT is_deleted</c> is <b>not</b> written here. The reflective soft-delete filter
    ///         supplies it (VC-13), and <c>IgnoreQueryFilters</c> is banned (conventions §7), so a
    ///         withdrawn correction cannot be counted.
    ///     </para>
    ///     <para>
    ///         Students with no absences are <b>absent from the result</b> rather than present with
    ///         zero — a grouped projection has no row to produce for them. Callers read through
    ///         <c>TryGetValue</c>; an indexer lookup throws for the commonest case in the system.
    ///     </para>
    /// </remarks>
    public static IQueryable<AbsenceCount> PriorAbsenceCounts(
        IQueryable<StudentAttendance> attendances,
        IReadOnlyCollection<Guid> studentIds,
        SchoolYear schoolYear,
        DateOnly excludedDate)
    {
        ArgumentNullException.ThrowIfNull(attendances);
        ArgumentNullException.ThrowIfNull(studentIds);

        (DateOnly from, DateOnly toExclusive) = schoolYear.ToDateRange();

        return attendances
            .Where(attendance => studentIds.Contains(attendance.StudentId)
                                 && attendance.AttendDate >= from
                                 && attendance.AttendDate < toExclusive
                                 && attendance.AttendDate != excludedDate
                                 && attendance.IsAbsent)
            .GroupBy(attendance => attendance.StudentId)
            .Select(group => new AbsenceCount(group.Key, group.Count()));
    }
}
