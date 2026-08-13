using domain.Abstraction;

namespace domain.AttendanceCodes;

/// <summary>
///     A code a teacher can record against a student for a day.
/// </summary>
/// <remarks>
///     Global rather than school-scoped, matching the legacy table and the route that exposes it.
///     <para>
///         <see cref="IsAbsent" /> and <see cref="IsExcused" /> are copied onto each attendance row at
///         save time rather than joined on read. Changing a code's meaning therefore does not rewrite
///         history — which is deliberate, and the reason nothing may recompute those flags from here.
///     </para>
/// </remarks>
public sealed class AttendanceCode : BaseEntity, ILegacyEntity
{
    /// <summary>
    ///     The short code itself, unique across active and inactive codes.
    /// </summary>
    /// <remarks>
    ///     Uppercase by check constraint. SQL Server's case-insensitive collation treated <c>A</c> and
    ///     <c>a</c> as one code; a Postgres unique index does not, so without the constraint two rows
    ///     that legacy considered duplicates could both exist.
    /// </remarks>
    public required string Value { get; set; }

    public required string Description { get; set; }

    public bool IsAbsent { get; set; }

    public bool IsExcused { get; set; }

    public bool IsActive { get; set; } = true;

    public int? LegacyId { get; set; }
}
