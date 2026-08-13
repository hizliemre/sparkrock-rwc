using domain.Abstraction;

namespace domain.Students;

/// <summary>
///     A student enrolled at a school.
/// </summary>
/// <remarks>
///     <see cref="SchoolId" /> is a single mutable foreign key with no enrolment history, which is a
///     documented limitation rather than an oversight: after a transfer, back-dated correction for
///     the former school is refused. A point-in-time enrolment entity would fix it and is deferred.
///     <para>
///         Date of birth is deliberately not modelled. Nothing in the feature set reads it, no
///         supplied legacy artifact reads it, and name plus date of birth plus school is the
///         highest-value combination in the dataset.
///     </para>
/// </remarks>
public sealed class Student : BaseEntity, ILegacyEntity, ISchoolScoped, IActivatable
{
    public required Guid SchoolId { get; set; }

    public required string FirstName { get; set; }

    public required string LastName { get; set; }

    /// <summary>
    ///     Nullable, matching legacy. A null grade is what made the old roster screen throw when it
    ///     populated its filter, and is why filtering by grade is optional rather than required.
    /// </summary>
    public string? Grade { get; set; }

    public bool IsActive { get; set; } = true;

    public int? LegacyId { get; set; }
}
