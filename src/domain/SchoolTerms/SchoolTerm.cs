using domain.Abstraction;

namespace domain.SchoolTerms;

/// <summary>
///     A named date range within a school's year.
/// </summary>
/// <remarks>
///     Bounds are <b>closed</b> — <c>[StartDate, EndDate]</c> — which is the one deliberate exception
///     to the half-open ranges used everywhere else. Legacy resolved a term with <c>BETWEEN</c>, and
///     that inclusive semantic is preserved; reading <see cref="EndDate" /> as exclusive would shift
///     every term by a day.
///     <para>
///         Overlapping active terms are rejected at write time. Legacy had no such constraint and its
///         term lookup took an arbitrary match, so resolution was nondeterministic under overlap.
///     </para>
/// </remarks>
public sealed class SchoolTerm : BaseEntity, IActivatable, ILegacyEntity, ISchoolScoped
{
    public required Guid SchoolId { get; set; }

    public required string Name { get; set; }

    public required DateOnly StartDate { get; set; }

    /// <summary>Inclusive. See the type remarks.</summary>
    public required DateOnly EndDate { get; set; }

    public bool IsActive { get; set; } = true;

    public int? LegacyId { get; set; }
}
