using domain.Abstraction;

namespace domain.Schools;

/// <summary>
///     A school. Reference data: deactivated, never deleted.
/// </summary>
/// <remarks>
///     Derives from <see cref="BaseEntity" /> rather than <see cref="SoftDeletableEntity" />, so it
///     carries no soft-delete columns and the reflective query filter does not touch it. That is what
///     stops deactivating a school making its students vanish from every projection that joins
///     through it.
/// </remarks>
public sealed class School : BaseEntity, ILegacyEntity
{
    public required string Name { get; set; }

    /// <summary>
    ///     IANA timezone identifying the school's local day.
    /// </summary>
    /// <remarks>
    ///     Required, and present in the first migration rather than added later, because the
    ///     school-year boundary and "today" are both resolved in school-local time — a UTC-derived
    ///     date rolls over mid-afternoon for many schools.
    /// </remarks>
    public required string TimeZoneId { get; set; }

    /// <summary>
    ///     Absences at which a chronic-absence alert is raised, or null to use the default.
    /// </summary>
    /// <remarks>
    ///     Nullable with no database default on purpose. A <c>DEFAULT 10</c> column would be a second
    ///     copy of a rule that already lives in one place, which is the duplication this migration
    ///     exists to remove.
    /// </remarks>
    public int? AbsenceAlertThreshold { get; set; }

    public bool IsActive { get; set; } = true;

    public int? LegacyId { get; set; }
}
