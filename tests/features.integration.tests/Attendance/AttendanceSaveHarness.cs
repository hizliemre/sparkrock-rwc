using System.Globalization;
using domain;
using domain.Alerts;
using domain.Attendance;
using domain.AttendanceCodes;
using domain.Schools;
using domain.Security;
using domain.Students;
using domain.ValueObjects;
using features.Attendance;
using features.integration.tests.Fakes;
using infra.persistence.postgre;
using infra.persistence.postgre.Interceptors;
using infra.persistence.sql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace features.integration.tests.Attendance;

/// <summary>
///     A school, a roster and its own attendance codes on the shared container database, plus the
///     handler wired to them and the hooks that make a race happen at the right instant.
/// </summary>
/// <remarks>
///     Every fixture is keyed on fresh <see cref="Guid" />s and every assertion is scoped to them: the
///     container's database is shared by the whole collection, no test may assume an empty table, and
///     none truncates.
///     <para>
///         <b>Attendance code values are randomised.</b> <c>ix_attendance_codes_value</c> is unique and
///         <em>unfiltered</em>, so a fixture seeding a literal <c>"A"</c> would collide with the next
///         one. <c>ck_attendance_codes_value_upper</c> additionally requires the value to be upper
///         case.
///     </para>
/// </remarks>
internal sealed class AttendanceSaveHarness : IAsyncDisposable
{
    public const string TimeZoneId = "America/Toronto";

    public static readonly DateTimeOffset Now = new(2026, 9, 14, 8, 0, 0, TimeSpan.Zero);

    public static readonly DateOnly SubmittedDate = new(2026, 9, 14);

    private readonly PostgresContainerFixture _fixture;

    private readonly List<SparkrockRwcDbContext> _contexts = [];

    private AttendanceSaveHarness(PostgresContainerFixture fixture, Guid schoolId)
    {
        _fixture = fixture;
        SchoolId = schoolId;
        CurrentUser = FakeCurrentUser.ScopedTo(schoolId);
        Clock = new FakeTimeProvider(Now);
        DbContext = NewContext();
    }

    public FakeTimeProvider Clock { get; }

    public ICurrentUser CurrentUser { get; }

    public SparkrockRwcDbContext DbContext { get; }

    public Guid SchoolId { get; }

    public string ConnectionString => _fixture.ConnectionString;

    /// <summary>Absent, unexcused.</summary>
    public AttendanceCode AbsentCode { get; private set; } = null!;

    /// <summary>Present.</summary>
    public AttendanceCode PresentCode { get; private set; } = null!;

    public static SchoolYear SchoolYear => domain.ValueObjects.SchoolYear.FromLocalDate(SubmittedDate);

    public static async Task<AttendanceSaveHarness> CreateAsync(
        PostgresContainerFixture fixture,
        int? absenceAlertThreshold = 10)
    {
        AttendanceSaveHarness harness = new(fixture, Guid.NewGuid());

        harness.DbContext.Schools.Add(new School
        {
            Id = harness.SchoolId,
            Name = "Integration School",
            TimeZoneId = TimeZoneId,
            AbsenceAlertThreshold = absenceAlertThreshold,
            IsActive = true
        });

        harness.AbsentCode = AttendanceSaveHarness.NewCode(isAbsent: true, description: "Absent — unexcused");
        harness.PresentCode = AttendanceSaveHarness.NewCode(isAbsent: false, description: "Present");

        harness.DbContext.AttendanceCodes.Add(harness.AbsentCode);
        harness.DbContext.AttendanceCodes.Add(harness.PresentCode);

        await harness.DbContext.SaveChangesAsync(CancellationToken.None);

        return harness;
    }

    /// <summary>A second context on its own connection — the other writer in every race below.</summary>
    public SparkrockRwcDbContext NewContext(IInterceptor[]? interceptors = null)
    {
        AuditableEntityInterceptor audit = new(CurrentUser, Clock, new AuditOverride());

        DbContextOptionsBuilder<SparkrockRwcDbContext> options = new DbContextOptionsBuilder<SparkrockRwcDbContext>()
            .UseNpgsql(_fixture.ConnectionString)

            // Must match WithPostgre and the design-time factory, or the migration creates snake_case
            // tables the tests then query as PascalCase.
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(audit);

        if (interceptors is not null)
            options.AddInterceptors(interceptors);

        SparkrockRwcDbContext context = new(
            options.Options,
            new infra.persistence.postgre.ErrorTranslation.ConstraintErrorRegistry(
                infra.persistence.postgre.ErrorTranslation.SchemaConstraintErrors.Mappings));

        _contexts.Add(context);

        return context;
    }

    public async Task<Student> AddStudentAsync(Guid? schoolId = null)
    {
        Student student = new()
        {
            Id = Guid.NewGuid(),
            SchoolId = schoolId ?? SchoolId,
            FirstName = "Demo",
            LastName = "Student",
            Grade = "09",
            IsActive = true
        };

        DbContext.Students.Add(student);
        await DbContext.SaveChangesAsync(CancellationToken.None);

        return student;
    }

    public async Task<School> AddSchoolAsync()
    {
        School school = new()
        {
            Id = Guid.NewGuid(),
            Name = "Other School",
            TimeZoneId = TimeZoneId,
            AbsenceAlertThreshold = 10,
            IsActive = true
        };

        DbContext.Schools.Add(school);
        await DbContext.SaveChangesAsync(CancellationToken.None);

        return school;
    }

    public StudentAttendance NewAttendance(
        Guid studentId,
        DateOnly attendDate,
        Guid? schoolId = null,
        bool isAbsent = true) =>
        new()
        {
            Id = Guid.NewGuid(),
            StudentId = studentId,
            SchoolId = schoolId ?? SchoolId,
            AttendDate = attendDate,
            AttendanceCodeId = isAbsent ? AbsentCode.Id : PresentCode.Id,
            AttendCode = isAbsent ? AbsentCode.Value : PresentCode.Value,
            AttendCodeDescription = "Seeded",
            IsAbsent = isAbsent,
            IsExcused = false
        };

    public async Task<StudentAttendance> AddAttendanceAsync(
        Guid studentId,
        DateOnly attendDate,
        Guid? schoolId = null,
        bool isAbsent = true)
    {
        StudentAttendance attendance = NewAttendance(studentId, attendDate, schoolId, isAbsent);

        DbContext.StudentAttendances.Add(attendance);
        await DbContext.SaveChangesAsync(CancellationToken.None);

        return attendance;
    }

    public async Task<StudentAttendanceSummary> AddSummaryAsync(Guid studentId, int totalAbsences)
    {
        StudentAttendanceSummary summary = new()
        {
            Id = Guid.NewGuid(),
            StudentId = studentId,
            SchoolId = SchoolId,
            SchoolYearStart = SchoolYear,
            TotalAbsences = totalAbsences
        };

        DbContext.StudentAttendanceSummaries.Add(summary);
        await DbContext.SaveChangesAsync(CancellationToken.None);

        return summary;
    }

    public SaveDailyAttendance.CommandHandler Handler(IDbContext? dbContext = null, int backDatingWindowDays = 30) =>
        new(
            dbContext ?? DbContext,
            CurrentUser,
            Clock,
            Options.Create(new AttendanceSaveOptions { BackDatingWindowDays = backDatingWindowDays }),
            NullLogger<SaveDailyAttendance.CommandHandler>.Instance);

    public SaveDailyAttendance.Command Command(
        IReadOnlyList<SaveDailyAttendance.Entry> entries,
        DateOnly? attendDate = null,
        string? idempotencyKey = null,
        Guid? schoolId = null) =>
        new()
        {
            SchoolId = schoolId ?? SchoolId,
            Date = (attendDate ?? SubmittedDate).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            IdempotencyKey = idempotencyKey,
            Entries = entries
        };

    public SaveDailyAttendance.Entry Entry(Guid studentId, bool absent = true, int? minutesLate = null) =>
        new()
        {
            StudentId = studentId,
            AttendCode = absent ? AbsentCode.Value : PresentCode.Value,
            MinutesLate = minutesLate
        };

    public async ValueTask DisposeAsync()
    {
        foreach (SparkrockRwcDbContext context in _contexts)
            await context.DisposeAsync();
    }

    /// <summary>
    ///     A code value nothing else in the shared database can already hold.
    /// </summary>
    private static AttendanceCode NewCode(bool isAbsent, string description) => new()
    {
        Id = Guid.NewGuid(),
        Value = Guid.NewGuid().ToString("N")[..5].ToUpperInvariant(),
        Description = description,
        IsAbsent = isAbsent,
        IsExcused = false,
        IsActive = true
    };
}

/// <summary>
///     An <see cref="IDbContext" /> that counts saves and can run another writer just before one.
/// </summary>
/// <remarks>
///     This is how a race is made to happen at the instant that matters. <c>BeforeSave</c> runs on a
///     <em>separate connection</em> and commits, so by the time the handler's single
///     <c>SaveChangesAsync</c> reaches the server the conflicting row is really there and the
///     handler's own <c>xmin</c> original value is really stale — neither of which EF InMemory can
///     produce (VC-35).
///     <para>
///         Conventions §6 bans mocking packages, so it is a hand-written decorator. It injects no
///         exceptions: every failure asserted in this tier comes from PostgreSQL.
///     </para>
/// </remarks>
internal sealed class RacingDbContext(SparkrockRwcDbContext inner) : IDbContext
{
    public Action<int>? BeforeSave { get; init; }

    public Action<int, Exception>? OnSaveFailed { get; init; }

    public int SaveChangesCalls { get; private set; }

    public DbSet<School> Schools
    {
        get => inner.Schools;
        set => inner.Schools = value;
    }

    public DbSet<Student> Students
    {
        get => inner.Students;
        set => inner.Students = value;
    }

    public DbSet<AttendanceCode> AttendanceCodes
    {
        get => inner.AttendanceCodes;
        set => inner.AttendanceCodes = value;
    }

    public DbSet<domain.SchoolTerms.SchoolTerm> SchoolTerms
    {
        get => inner.SchoolTerms;
        set => inner.SchoolTerms = value;
    }

    public DbSet<StudentAttendance> StudentAttendances
    {
        get => inner.StudentAttendances;
        set => inner.StudentAttendances = value;
    }

    public DbSet<StudentAttendanceSummary> StudentAttendanceSummaries
    {
        get => inner.StudentAttendanceSummaries;
        set => inner.StudentAttendanceSummaries = value;
    }

    public DbSet<StudentAlert> StudentAlerts
    {
        get => inner.StudentAlerts;
        set => inner.StudentAlerts = value;
    }

    public DbSet<AttendanceSubmissionLog> AttendanceSubmissionLogs
    {
        get => inner.AttendanceSubmissionLogs;
        set => inner.AttendanceSubmissionLogs = value;
    }

    public DbSet<TestEntity> TestEntities
    {
        get => inner.TestEntities;
        set => inner.TestEntities = value;
    }

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        SaveChangesCalls++;

        BeforeSave?.Invoke(SaveChangesCalls);

        try
        {
            return await inner.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            OnSaveFailed?.Invoke(SaveChangesCalls, exception);
            throw;
        }
    }
}
