using domain.AttendanceCodes;
using infra.persistence.postgre;

namespace features.tests.AttendanceCodes;

/// <summary>
///     Inserts an <see cref="AttendanceCode" /> through the real context, so the audit interceptor
///     stamps it.
/// </summary>
/// <remarks>
///     Audit fields are never hand-set (DEC-21) — a test that needs a particular <c>CreatedAt</c>
///     advances the <c>FakeTimeProvider</c> instead.
///     <para>
///         The seed writes <paramref name="value" /> verbatim rather than normalising it. Seeding
///         through <c>AttendanceCodeValue.Normalise</c> would make a handler that forgot to normalise
///         indistinguishable from one that did.
///     </para>
/// </remarks>
internal static class AttendanceCodeSeed
{
    public const string DefaultValue = "A";

    public const string DefaultDescription = "Absent — unexcused";

    public static async Task<AttendanceCode> AddAsync(
        SparkrockRwcDbContext dbContext,
        Guid? id = null,
        string value = DefaultValue,
        string description = DefaultDescription,
        bool isAbsent = true,
        bool isExcused = false,
        bool isActive = true)
    {
        AttendanceCode code = new()
        {
            Id = id ?? Guid.NewGuid(),
            Value = value,
            Description = description,
            IsAbsent = isAbsent,
            IsExcused = isExcused,
            IsActive = isActive
        };

        dbContext.AttendanceCodes.Add(code);
        await dbContext.SaveChangesAsync(CancellationToken.None);

        return code;
    }
}
