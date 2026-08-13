using System.Collections.Generic;
using domain.AttendanceCodes;
using domain.Schools;
using domain.SchoolTerms;
using domain.Students;

namespace tools.seed;

/// <summary>
///     The whole seeded dataset, as entities, with no I/O performed and no clock read.
/// </summary>
/// <remarks>
///     The plan carries <b>entities</b> rather than DTOs. They are ordinary constructible objects
///     whose only non-public setters are the audit fields (DEC-21), so there is nothing to map and no
///     second shape to keep in sync with the model.
///     <para>
///         Because <see cref="SeedCatalog.Build" /> is pure, every content rule — non-overlapping
///         terms, uppercase code values, the deliberate term gaps, the reserved id prefix — is
///         assertable with no database, no host and no clock.
///     </para>
/// </remarks>
public sealed record SeedPlan
{
    public required School School { get; init; }

    public required IReadOnlyList<AttendanceCode> AttendanceCodes { get; init; }

    public required IReadOnlyList<SchoolTerm> Terms { get; init; }

    public required IReadOnlyList<Student> Students { get; init; }
}
