using domain.Alerts;
using domain.Schools;
using domain.Security;
using domain.Students;
using domain.ValueObjects;
using features.integration.tests.Fakes;
using infra.persistence.postgre;
using infra.persistence.postgre.ErrorTranslation;
using infra.persistence.postgre.Interceptors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace features.integration.tests.Alerts;

/// <summary>
///     Seeds a school, a student and alerts on the shared container database.
/// </summary>
/// <remarks>
///     Every fixture is keyed on fresh <see cref="Guid" />s and every assertion is scoped to them: the
///     container's database is shared by the whole collection, no test may assume an empty table, and
///     none truncates.
///     <para>
///         School years are allocated well outside the range the other suites use, so an alert seeded
///         here can never collide with one of theirs on
///         <c>ix_student_alerts_open_episode</c> — which is the very index two of these tests are
///         about.
///     </para>
/// </remarks>
internal static class AlertFixture
{
    public const string TimeZoneId = "America/Toronto";

    /// <summary>
    ///     A context whose constraint violations arrive as domain exceptions.
    /// </summary>
    /// <remarks>
    ///     <b><see cref="ContainerDbContextFactory" /> does not supply an
    ///     <see cref="IConstraintErrorRegistry" /></b>, so a context built through it falls back to
    ///     <c>ConstraintErrorRegistry.Empty</c> and <c>SaveChangesAsync</c>'s translation matches
    ///     nothing — a <c>23505</c> surfaces as a raw <see cref="DbUpdateException" />. That is
    ///     correct for every test that does not assert on the translation and a trap for every test
    ///     that does: the assertion fails on the exception <em>type</em>, which reads as a missing
    ///     constraint rather than as a missing registry. F07's harness builds its own context for the
    ///     same reason; this is the second copy, and the shared factory is where the fix belongs.
    /// </remarks>
    public static SparkrockRwcDbContext TranslatingContext(string connectionString)
    {
        AuditableEntityInterceptor audit = new(
            new FakeCurrentUser(),
            new FakeTimeProvider(ContainerDbContextFactory.DefaultNow),
            new AuditOverride());

        DbContextOptions<SparkrockRwcDbContext> options = new DbContextOptionsBuilder<SparkrockRwcDbContext>()
            .UseNpgsql(connectionString)

            // Must match WithPostgre and the design-time factory, or the migration creates snake_case
            // tables the tests then query as PascalCase.
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(audit)
            .Options;

        return new SparkrockRwcDbContext(options, new ConstraintErrorRegistry(SchemaConstraintErrors.Mappings));
    }

    public static async Task<School> SchoolAsync(SparkrockRwcDbContext dbContext, int? threshold)
    {
        School school = new()
        {
            Id = Guid.NewGuid(),
            Name = $"Alerts {Guid.NewGuid()}",
            TimeZoneId = TimeZoneId,
            AbsenceAlertThreshold = threshold
        };

        dbContext.Schools.Add(school);
        await dbContext.SaveChangesAsync(CancellationToken.None);

        return school;
    }

    public static async Task<Student> StudentAsync(SparkrockRwcDbContext dbContext, Guid schoolId)
    {
        Student student = new()
        {
            Id = Guid.NewGuid(),
            SchoolId = schoolId,
            FirstName = "Alert",
            LastName = "Subject"
        };

        dbContext.Students.Add(student);
        await dbContext.SaveChangesAsync(CancellationToken.None);

        return student;
    }

    /// <summary>Tracks an open episode without saving, so a caller can batch two into one save.</summary>
    public static StudentAlert NewOpenAlert(Guid studentId, Guid schoolId, int schoolYearStart, int thresholdAtRaise) =>
        new()
        {
            Id = Guid.NewGuid(),
            StudentId = studentId,
            SchoolId = schoolId,
            AlertType = AlertType.ChronicAbsence,
            SchoolYearStart = SchoolYear.FromStartYear(schoolYearStart),
            AbsenceCount = thresholdAtRaise + 1,
            ThresholdAtRaise = thresholdAtRaise
        };

    public static async Task<StudentAlert> OpenAlertAsync(
        SparkrockRwcDbContext dbContext,
        Guid studentId,
        Guid schoolId,
        int schoolYearStart,
        int thresholdAtRaise = 10)
    {
        StudentAlert alert = NewOpenAlert(studentId, schoolId, schoolYearStart, thresholdAtRaise);

        dbContext.StudentAlerts.Add(alert);
        await dbContext.SaveChangesAsync(CancellationToken.None);

        return alert;
    }
}
