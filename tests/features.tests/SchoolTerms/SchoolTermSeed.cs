using domain.SchoolTerms;
using infra.persistence.postgre;

namespace features.tests.SchoolTerms;

/// <summary>
///     Inserts a <see cref="SchoolTerm" /> through the real context, so the audit interceptor stamps
///     it.
/// </summary>
/// <remarks>
///     Audit fields are never hand-set (DEC-21) — a test that needs a particular <c>CreatedAt</c>
///     advances the <c>FakeTimeProvider</c> instead.
/// </remarks>
internal static class SchoolTermSeed
{
    public const string DefaultName = "Term 1";

    /// <summary>First day, inclusive.</summary>
    public static readonly DateOnly DefaultStart = new(2026, 9, 1);

    /// <summary>Last day, <b>inclusive</b> — closed bounds (F01c §3).</summary>
    public static readonly DateOnly DefaultEnd = new(2026, 12, 20);

    public static async Task<SchoolTerm> AddAsync(
        SparkrockRwcDbContext dbContext,
        Guid schoolId,
        Guid? id = null,
        string name = DefaultName,
        DateOnly? startDate = null,
        DateOnly? endDate = null,
        bool isActive = true)
    {
        SchoolTerm term = new()
        {
            Id = id ?? Guid.NewGuid(),
            SchoolId = schoolId,
            Name = name,
            StartDate = startDate ?? DefaultStart,
            EndDate = endDate ?? DefaultEnd,
            IsActive = isActive
        };

        dbContext.SchoolTerms.Add(term);
        await dbContext.SaveChangesAsync(CancellationToken.None);

        return term;
    }
}
