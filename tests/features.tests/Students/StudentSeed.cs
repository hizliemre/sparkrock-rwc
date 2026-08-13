using domain.Students;
using infra.persistence.postgre;

namespace features.tests.Students;

/// <summary>
///     Inserts a <see cref="Student" /> through the real context, so the audit interceptor stamps it.
/// </summary>
/// <remarks>
///     Audit fields are never hand-set (DEC-21) — a test that needs a particular <c>CreatedAt</c> or
///     <c>ModifiedAt</c> advances the <c>FakeTimeProvider</c> instead.
/// </remarks>
internal static class StudentSeed
{
    public const string DefaultFirstName = "Demo";

    public const string DefaultLastName = "Student01";

    public const string DefaultGrade = "09";

    public static async Task<Student> AddAsync(
        SparkrockRwcDbContext dbContext,
        Guid schoolId,
        Guid? id = null,
        string firstName = DefaultFirstName,
        string lastName = DefaultLastName,
        string? grade = DefaultGrade,
        bool isActive = true,
        int? legacyId = null)
    {
        Student student = new()
        {
            Id = id ?? Guid.NewGuid(),
            SchoolId = schoolId,
            FirstName = firstName,
            LastName = lastName,
            Grade = grade,
            IsActive = isActive,
            LegacyId = legacyId
        };

        dbContext.Students.Add(student);
        await dbContext.SaveChangesAsync(CancellationToken.None);

        return student;
    }
}
