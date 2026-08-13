using domain.Alerts;
using domain.Attendance;
using domain.AttendanceCodes;
using domain.Schools;
using domain.SchoolTerms;
using domain.Security;
using domain.Students;
using domain.ValueObjects;
using features.Attendance;
using features.tests.AttendanceCodes;
using features.tests.Fakes;
using features.tests.Schools;
using features.tests.Students;
using infra.persistence.postgre;
using infra.persistence.postgre.Interceptors;
using infra.persistence.sql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace features.tests.Attendance;

/// <summary>
///     A school, a roster and the five attendance codes, on one in-memory database, plus the handler
///     wired to them.
/// </summary>
/// <remarks>
///     Builds its own fixtures with fresh <see cref="Guid" />s and reads no seed data — F00 seeds one
///     school, so V-07c (cross-school counts) and V-13 (transfer) have no seed data to work from
///     anyway, and F01f's shared-database rule forbids assuming rows exist.
///     <para>
///         Unlike <c>InMemoryDbContextFactory</c> this keeps the database <em>name</em>, so
///         <see cref="NewContext" /> can open a second context over the same store. Two contexts is
///         what a "another writer got there first" handler-tier test needs — though note that no
///         assertion about concurrency or retry means anything at this tier (VC-35): the
///         <c>xmin</c> token is built and never populated, so every check passes trivially. The second
///         context here is used only to place a row a *later* read must find, never to prove a race.
///     </para>
///     <para>
///         The identity is a non-admin scoped to the fixture's school. Copying the production stub's
///         <c>IsSystemAdmin = true</c> would let a handler drop <c>EnsureAuthorized</c> without a test
///         failing.
///     </para>
/// </remarks>
internal sealed class AttendanceSubmissionFixture : IAsyncDisposable
{
    public const string TimeZoneId = "America/Toronto";

    /// <summary>Absent, unexcused.</summary>
    public const string AbsentCode = "A";

    /// <summary>Absent, excused.</summary>
    public const string ExcusedCode = "E";

    /// <summary>Present.</summary>
    public const string PresentCode = "P";

    /// <summary>Present, late — the code that carries <c>MinutesLate</c>.</summary>
    public const string LateCode = "L";

    /// <summary>Exists and is deactivated, so it must read exactly like an unknown code.</summary>
    public const string InactiveCode = "X";

    /// <summary>A code no fixture ever creates.</summary>
    public const string UnknownCode = "Z";

    private readonly string _databaseName = Guid.NewGuid().ToString();

    private readonly List<SparkrockRwcDbContext> _contexts = [];

    private AttendanceSubmissionFixture(FakeTimeProvider clock, ICurrentUser currentUser)
    {
        Clock = clock;
        CurrentUser = currentUser;
        DbContext = NewContext();
    }

    public FakeTimeProvider Clock { get; }

    public ICurrentUser CurrentUser { get; }

    public SparkrockRwcDbContext DbContext { get; }

    public Guid SchoolId { get; private set; }

    public School School { get; private set; } = null!;

    public Dictionary<string, AttendanceCode> Codes { get; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     The default submitted date, which is school-local <em>today</em> under the default clock.
    /// </summary>
    /// <remarks>
    ///     <c>2026-09-14T08:00Z</c> is <c>04:00</c> in America/Toronto, so the school's day is the
    ///     14th. Resolving it as <c>UtcNow.Date</c> happens to agree here — the test that separates the
    ///     two uses <see cref="CreateAsync" />'s <c>utcNow</c> parameter to put the clock at
    ///     <c>02:00Z</c>, where school-local is still the 13th.
    /// </remarks>
    public static readonly DateOnly SubmittedDate = new(2026, 9, 14);

    public static SchoolYear SchoolYear => domain.ValueObjects.SchoolYear.FromLocalDate(SubmittedDate);

    public static async Task<AttendanceSubmissionFixture> CreateAsync(
        DateTimeOffset? utcNow = null,
        int? absenceAlertThreshold = 10,
        bool schoolIsActive = true,
        bool systemAdmin = false,
        bool seedInactiveCode = true)
    {
        Guid schoolId = Guid.NewGuid();

        FakeTimeProvider clock = new(utcNow ?? InMemoryDbContextFactory.DefaultNow);

        ICurrentUser currentUser = systemAdmin
            ? FakeCurrentUser.SystemAdmin()
            : FakeCurrentUser.ScopedTo(schoolId);

        AttendanceSubmissionFixture fixture = new(clock, currentUser) { SchoolId = schoolId };

        fixture.School = await SchoolSeed.AddAsync(
            fixture.DbContext,
            schoolId,
            timeZoneId: TimeZoneId,
            threshold: absenceAlertThreshold,
            isActive: schoolIsActive);

        await fixture.AddCodeAsync(AbsentCode, "Absent — unexcused", isAbsent: true, isExcused: false);
        await fixture.AddCodeAsync(ExcusedCode, "Absent — excused", isAbsent: true, isExcused: true);
        await fixture.AddCodeAsync(PresentCode, "Present", isAbsent: false, isExcused: false);
        await fixture.AddCodeAsync(LateCode, "Late", isAbsent: false, isExcused: false);
        // Skippable, because DEC-20 makes an AttendanceCode undeletable — the interceptor throws on
        // Remove(). Proving that an inactive code and an unknown one produce identical violations
        // therefore needs two databases, one where the value exists and is deactivated and one where
        // it was never created, submitting the *same* string to both.
        if (seedInactiveCode)
            await fixture.AddCodeAsync(InactiveCode, "Retired code", isAbsent: true, isExcused: false, isActive: false);

        return fixture;
    }

    /// <summary>A second context over the same in-memory store.</summary>
    public SparkrockRwcDbContext NewContext()
    {
        AuditableEntityInterceptor interceptor = new(CurrentUser, Clock, new AuditOverride());

        DbContextOptions<SparkrockRwcDbContext> options = new DbContextOptionsBuilder<SparkrockRwcDbContext>()
            .UseInMemoryDatabase(_databaseName)
            .AddInterceptors(interceptor)
            .Options;

        SparkrockRwcDbContext context = new(options);
        _contexts.Add(context);

        return context;
    }

    public Task<Student> AddStudentAsync(Guid? schoolId = null, bool isActive = true, string? grade = "09") =>
        StudentSeed.AddAsync(
            DbContext,
            schoolId ?? SchoolId,
            firstName: "Demo",
            lastName: "Student" + Guid.NewGuid().ToString("N")[..6],
            grade: grade,
            isActive: isActive);

    public Task<SchoolTerm> AddTermAsync(DateOnly startDate, DateOnly endDate, bool isActive = true, Guid? id = null)
    {
        SchoolTerm term = new()
        {
            Id = id ?? Guid.NewGuid(),
            SchoolId = SchoolId,
            Name = "Term",
            StartDate = startDate,
            EndDate = endDate,
            IsActive = isActive
        };

        DbContext.SchoolTerms.Add(term);

        return SaveAndReturnAsync(term);
    }

    /// <summary>
    ///     Seeds an existing attendance row. <paramref name="schoolId" /> defaults to the fixture's
    ///     school; passing another one is how V-06's global <c>(StudentId, AttendDate)</c> key is
    ///     exercised.
    /// </summary>
    public Task<StudentAttendance> AddAttendanceAsync(
        Guid studentId,
        DateOnly attendDate,
        bool isAbsent = true,
        Guid? schoolId = null,
        Guid? submissionId = null,
        string attendCode = AbsentCode,
        string? notes = null,
        int? minutesLate = null)
    {
        StudentAttendance attendance = new()
        {
            Id = Guid.NewGuid(),
            StudentId = studentId,
            SchoolId = schoolId ?? SchoolId,
            AttendDate = attendDate,
            AttendanceCodeId = Codes.TryGetValue(attendCode, out AttendanceCode? code) ? code.Id : Guid.NewGuid(),
            SubmissionId = submissionId,
            AttendCode = attendCode,
            AttendCodeDescription = "Seeded",
            IsAbsent = isAbsent,
            IsExcused = false,
            MinutesLate = minutesLate,
            Notes = notes
        };

        DbContext.StudentAttendances.Add(attendance);

        return SaveAndReturnAsync(attendance);
    }

    public Task<StudentAttendanceSummary> AddSummaryAsync(Guid studentId, int totalAbsences, Guid? schoolId = null)
    {
        StudentAttendanceSummary summary = new()
        {
            Id = Guid.NewGuid(),
            StudentId = studentId,
            SchoolId = schoolId ?? SchoolId,
            SchoolYearStart = SchoolYear,
            TotalAbsences = totalAbsences
        };

        DbContext.StudentAttendanceSummaries.Add(summary);

        return SaveAndReturnAsync(summary);
    }

    public Task<StudentAlert> AddAlertAsync(
        Guid studentId,
        Guid? schoolId = null,
        int absenceCount = 10,
        DateTimeOffset? resolvedAt = null,
        ResolutionSource? resolutionSource = null)
    {
        StudentAlert alert = new()
        {
            Id = Guid.NewGuid(),
            StudentId = studentId,
            SchoolId = schoolId ?? SchoolId,
            AlertType = AlertType.ChronicAbsence,
            SchoolYearStart = SchoolYear,
            AbsenceCount = absenceCount,
            ThresholdAtRaise = 10,
            ResolvedAt = resolvedAt,
            ResolvedBy = resolvedAt is null ? null : Guid.NewGuid(),
            ResolutionSource = resolutionSource
        };

        DbContext.StudentAlerts.Add(alert);

        return SaveAndReturnAsync(alert);
    }

    /// <summary>The handler under test, over this fixture's context unless another is supplied.</summary>
    public SaveDailyAttendance.CommandHandler Handler(
        IDbContext? dbContext = null,
        int backDatingWindowDays = 30,
        RecordingLogger<SaveDailyAttendance.CommandHandler>? logger = null,
        ICurrentUser? currentUser = null) =>
        new(
            dbContext ?? DbContext,
            currentUser ?? CurrentUser,
            Clock,
            Options.Create(new AttendanceSaveOptions { BackDatingWindowDays = backDatingWindowDays }),
            logger ?? new RecordingLogger<SaveDailyAttendance.CommandHandler>());

    public SaveDailyAttendance.Command Command(
        params SaveDailyAttendance.Entry[] entries) =>
        CommandOn(SubmittedDate, entries);

    public SaveDailyAttendance.Command CommandOn(
        DateOnly attendDate,
        params SaveDailyAttendance.Entry[] entries) =>
        new()
        {
            SchoolId = SchoolId,
            Date = attendDate.ToString(GetAttendanceRoster.DateFormat, System.Globalization.CultureInfo.InvariantCulture),
            Entries = entries
        };

    public static SaveDailyAttendance.Entry Entry(
        Guid studentId,
        string attendCode = AbsentCode,
        int? minutesLate = null,
        string? notes = null) =>
        new()
        {
            StudentId = studentId,
            AttendCode = attendCode,
            MinutesLate = minutesLate,
            Notes = notes
        };

    public async ValueTask DisposeAsync()
    {
        foreach (SparkrockRwcDbContext context in _contexts)
            await context.DisposeAsync();
    }

    private async Task<AttendanceCode> AddCodeAsync(
        string value,
        string description,
        bool isAbsent,
        bool isExcused,
        bool isActive = true)
    {
        AttendanceCode code = await AttendanceCodeSeed.AddAsync(
            DbContext,
            value: value,
            description: description,
            isAbsent: isAbsent,
            isExcused: isExcused,
            isActive: isActive);

        Codes[value] = code;

        return code;
    }

    private async Task<TEntity> SaveAndReturnAsync<TEntity>(TEntity entity)
    {
        await DbContext.SaveChangesAsync(CancellationToken.None);

        return entity;
    }
}
