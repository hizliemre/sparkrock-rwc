using domain.Schools;
using infra.persistence.postgre;

namespace features.tests.Schools;

/// <summary>
///     Inserts a <see cref="School" /> through the real context, so the audit interceptor stamps it.
/// </summary>
/// <remarks>
///     Audit fields are never hand-set (DEC-21) — a test that needs a particular
///     <c>CreatedAt</c> advances the <c>FakeTimeProvider</c> instead.
/// </remarks>
internal static class SchoolSeed
{
    public const string DefaultName = "Rideau Demo School";

    public const string DefaultTimeZoneId = "America/Toronto";

    public static async Task<School> AddAsync(
        SparkrockRwcDbContext dbContext,
        Guid? id = null,
        string name = DefaultName,
        string timeZoneId = DefaultTimeZoneId,
        int? threshold = 12,
        bool isActive = true)
    {
        School school = new()
        {
            Id = id ?? Guid.NewGuid(),
            Name = name,
            TimeZoneId = timeZoneId,
            AbsenceAlertThreshold = threshold,
            IsActive = isActive
        };

        dbContext.Schools.Add(school);
        await dbContext.SaveChangesAsync(CancellationToken.None);

        return school;
    }
}
