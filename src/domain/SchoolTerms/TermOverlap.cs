using System.Linq.Expressions;

namespace domain.SchoolTerms;

/// <summary>
///     V-19's mechanism: the one predicate that decides whether a proposed term collides with an
///     existing one.
/// </summary>
/// <remarks>
///     In <c>domain/&lt;Aggregate&gt;/</c> because two slices need it — <c>CreateSchoolTerm</c> and
///     <c>UpdateSchoolTerm</c> — and conventions §3 puts logic shared by two slices in exactly one
///     place. Two inlined copies is how one of them ends up with <c>&lt;</c> where the other has
///     <c>&lt;=</c>, and the difference is one day per boundary.
///     <para>
///         Legacy resolved a date to a term with <c>SELECT @TermID = TermID … BETWEEN StartDate AND
///         EndDate</c> — no <c>TOP 1</c>, no ordering (D-03) — so two overlapping terms meant an
///         arbitrary one won, silently and differently per query plan. V-19's fix is not a
///         deterministic read; it is making the overlapping state unreachable.
///     </para>
/// </remarks>
public static class TermOverlap
{
    /// <summary>
    ///     Matches the active terms of <paramref name="schoolId" /> that share at least one day with
    ///     <c>[startDate, endDate]</c>.
    /// </summary>
    /// <remarks>
    ///     <b>Both bounds are closed on both sides</b>, so the comparisons are <c>&lt;=</c> and not
    ///     <c>&lt;</c>: two ranges intersect exactly when each starts on or before the other ends. A
    ///     term ending on the day the next begins <em>is</em> an overlap. This is F01c §3's one
    ///     deliberate exception to the system-wide half-open rule, preserved because D-03 keeps
    ///     legacy's <c>BETWEEN</c>, and it is stated in the OpenAPI description of both date fields.
    ///     <para>
    ///         Only <b>active</b> terms participate. An inactive term may overlap anything — that is
    ///         what makes deactivation the way to supersede a term and free its dates for a
    ///         replacement.
    ///     </para>
    ///     <para>
    ///         An <see cref="Expression{TDelegate}" /> rather than a <c>bool</c> method, because it has
    ///         to translate: a static predicate called inside <c>Where</c> compiles fine and fails at
    ///         EF's translation step, at run time, on the write path.
    ///     </para>
    ///     <para>
    ///         The probe is an index seek on
    ///         <c>ix_school_terms_school_id_start_date_end_date</c>, which is the only reason F01c
    ///         shipped that index.
    ///     </para>
    /// </remarks>
    /// <param name="schoolId">Scopes the probe. A term never collides with another school's calendar.</param>
    /// <param name="startDate">First day of the proposed term, inclusive.</param>
    /// <param name="endDate">Last day of the proposed term, inclusive.</param>
    /// <param name="excludingTermId">
    ///     The term being updated, so it does not conflict with itself. The create path has none and
    ///     passes <see cref="Guid.Empty" />, which is never a real key; a nullable parameter would emit
    ///     <c>@p IS NULL OR id &lt;&gt; @p</c> for no benefit.
    /// </param>
    public static Expression<Func<SchoolTerm, bool>> Overlapping(
        Guid schoolId,
        DateOnly startDate,
        DateOnly endDate,
        Guid excludingTermId) =>
        existing => existing.IsActive
                    && existing.SchoolId == schoolId
                    && existing.Id != excludingTermId
                    && existing.StartDate <= endDate
                    && startDate <= existing.EndDate;
}
