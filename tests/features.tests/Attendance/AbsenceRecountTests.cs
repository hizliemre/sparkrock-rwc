using System.Reflection;
using domain.Attendance;
using domain.ValueObjects;
using infra.persistence.postgre;
using Microsoft.EntityFrameworkCore;

namespace features.tests.Attendance;

/// <summary>
///     The shared recount predicate (T07-05), which design §5 requires to be extracted from F07 rather
///     than inlined because F12 recomputes every summary with it.
/// </summary>
/// <remarks>
///     Handler tier: every assertion here is about LINQ semantics, the reflective soft-delete filter
///     and the shape of the materialised result. None of it depends on relational behaviour.
///     <para>
///         <b>The two load-bearing assertions are the two exclusions.</b>
///         <c>PriorAbsenceCounts_ExcludesTheSubmittedDate</c> is what makes DEC-14's
///         read-then-compute-in-memory correct at all, and <c>…_SpansSchools</c> is what fails the
///         moment somebody "fixes" the query by adding the <c>school_id</c> predicate VC-13's verified
///         SQL carries.
///     </para>
/// </remarks>
public sealed class AbsenceRecountTests
{
    private static readonly SchoolYear Year = SchoolYear.FromStartYear(2026);

    private static readonly DateOnly SubmittedDate = new(2026, 9, 14);

    /// <summary>
    ///     The recount must not be able to grow a school predicate, and the strongest available guard
    ///     is that there is no school in the parameter list to pass one through.
    /// </summary>
    /// <remarks>
    ///     V-07c ●: absences follow the student across a transfer within the school year, so the count
    ///     a school reads includes absences accrued elsewhere. VC-13's verified SQL <em>does</em> carry
    ///     <c>s.school_id = @__schoolId_1</c>, and reading that entry as the query shape to copy
    ///     reinstates D-05's single-school ambiguity. This is a structural assertion because the
    ///     behavioural one (<see cref="PriorAbsenceCounts_SpansSchools" />) can be satisfied by a
    ///     caller that simply never passes a school.
    /// </remarks>
    [Fact]
    public void PriorAbsenceCounts_TakesNoSchoolParameter()
    {
        MethodInfo method = typeof(AbsenceRecount).GetMethod(nameof(AbsenceRecount.PriorAbsenceCounts))!;

        string[] schoolish = method.GetParameters()
            .Select(parameter => parameter.Name ?? string.Empty)
            .Where(name => name.Contains("school", StringComparison.OrdinalIgnoreCase)
                           && !name.Contains("schoolYear", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(
            schoolish.Length == 0,
            $"PriorAbsenceCounts grew a school-shaped parameter ({string.Join(", ", schoolish)}). "
            + "V-07c requires the count to span schools within the school year; VC-13's verified SQL "
            + "carries a school_id predicate and must not be copied here.");
    }

    /// <summary>
    ///     Case 3 and case 5 of spec §1 both rest on this. Today's row is still committed when the
    ///     count is read, so without the exclusion a student already marked absent today is counted
    ///     once by the query and once again by the in-memory <c>+1</c>.
    /// </summary>
    [Fact]
    public async Task PriorAbsenceCounts_ExcludesTheSubmittedDate()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await StudentAttendanceSeed.AddAsync(dbContext, studentId, schoolId, SubmittedDate);
        await StudentAttendanceSeed.AddAsync(dbContext, studentId, schoolId, SubmittedDate.AddDays(-7));

        Dictionary<Guid, int> counts = await CountsAsync(dbContext, [studentId]);

        Assert.Equal(1, counts[studentId]);
    }

    /// <summary>
    ///     <b>V-07b's supporting assertion.</b> Legacy's predicate compared a function of
    ///     <c>@AttendDate</c> to a value derived from <c>@AttendDate</c>, so it referenced no column and
    ///     filtered nothing (L-12) — the stored total was a lifetime count, a zero, or a mix.
    /// </summary>
    [Fact]
    public async Task PriorAbsenceCounts_ExcludesPriorSchoolYears()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await StudentAttendanceSeed.AddAsync(dbContext, studentId, schoolId, new DateOnly(2025, 6, 10));

        Dictionary<Guid, int> counts = await CountsAsync(dbContext, [studentId]);

        Assert.False(counts.ContainsKey(studentId));
    }

    /// <summary>
    ///     Half-open <c>[from, toExclusive)</c>, the rule conventions §2 states once for the whole API.
    /// </summary>
    [Fact]
    public async Task PriorAbsenceCounts_IncludesTheFirstAndExcludesTheLastDayOfTheRange()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        (DateOnly from, DateOnly toExclusive) = Year.ToDateRange();

        await StudentAttendanceSeed.AddAsync(dbContext, studentId, schoolId, from);
        await StudentAttendanceSeed.AddAsync(dbContext, studentId, schoolId, toExclusive);

        Dictionary<Guid, int> counts = await CountsAsync(dbContext, [studentId]);

        Assert.Equal(1, counts[studentId]);
    }

    [Fact]
    public async Task PriorAbsenceCounts_ExcludesPresentRows()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await StudentAttendanceSeed.AddAsync(
            dbContext, studentId, schoolId, SubmittedDate.AddDays(-1), isAbsent: false);

        Dictionary<Guid, int> counts = await CountsAsync(dbContext, [studentId]);

        Assert.False(counts.ContainsKey(studentId));
    }

    /// <summary>
    ///     <b>V-07c's supporting assertion.</b> The handler-level twin is in
    ///     <c>SaveDailyAttendanceHandlerTests</c>.
    /// </summary>
    [Fact]
    public async Task PriorAbsenceCounts_SpansSchools()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        Guid studentId = Guid.NewGuid();

        await StudentAttendanceSeed.AddAsync(dbContext, studentId, Guid.NewGuid(), SubmittedDate.AddDays(-3));
        await StudentAttendanceSeed.AddAsync(dbContext, studentId, Guid.NewGuid(), SubmittedDate.AddDays(-4));

        Dictionary<Guid, int> counts = await CountsAsync(dbContext, [studentId]);

        Assert.Equal(2, counts[studentId]);
    }

    /// <summary>
    ///     The reflective query filter supplies <c>NOT is_deleted</c> (VC-13); the recount writes
    ///     nothing to get it, and <c>IgnoreQueryFilters</c> is banned (conventions §7).
    /// </summary>
    /// <remarks>
    ///     Removed through <c>Remove()</c> + <c>SaveChangesAsync</c>, never by assigning
    ///     <c>IsDeleted</c> — DEC-21 makes the interceptor the only writer of that column.
    /// </remarks>
    [Fact]
    public async Task PriorAbsenceCounts_ExcludesSoftDeletedRows()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        StudentAttendance withdrawn = await StudentAttendanceSeed.AddAsync(
            dbContext, studentId, schoolId, SubmittedDate.AddDays(-2));

        dbContext.StudentAttendances.Remove(withdrawn);
        await dbContext.SaveChangesAsync(CancellationToken.None);

        Dictionary<Guid, int> counts = await CountsAsync(dbContext, [studentId]);

        Assert.False(counts.ContainsKey(studentId));
    }

    /// <summary>
    ///     The shape that makes an indexer lookup throw for the commonest case in the system — a
    ///     student with a clean record. Reads go through <c>TryGetValue</c>.
    /// </summary>
    [Fact]
    public async Task PriorAbsenceCounts_OmitsStudentsWithNoAbsences()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        Guid schoolId = Guid.NewGuid();
        Guid absent = Guid.NewGuid();
        Guid clean = Guid.NewGuid();

        await StudentAttendanceSeed.AddAsync(dbContext, absent, schoolId, SubmittedDate.AddDays(-1));

        Dictionary<Guid, int> counts = await CountsAsync(dbContext, [absent, clean]);

        Assert.True(counts.ContainsKey(absent));
        Assert.False(counts.ContainsKey(clean));
        Assert.Throws<KeyNotFoundException>(() => counts[clean]);
    }

    /// <summary>
    ///     <b>V-07a.</b> One grouped projection for the whole batch, never one query per student
    ///     (L-08). Asserted here by the returned shape — every submitted student's own count comes back
    ///     from one call; the command count itself is the integration tier's
    ///     <c>Handle_IssuesExactlyOneRecountQueryForTheWholeBatch</c>.
    /// </summary>
    [Fact]
    public async Task PriorAbsenceCounts_GroupsInOneQuery()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        Guid schoolId = Guid.NewGuid();
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();

        await StudentAttendanceSeed.AddAsync(dbContext, first, schoolId, SubmittedDate.AddDays(-1));
        await StudentAttendanceSeed.AddAsync(dbContext, first, schoolId, SubmittedDate.AddDays(-2));
        await StudentAttendanceSeed.AddAsync(dbContext, second, schoolId, SubmittedDate.AddDays(-1));

        Dictionary<Guid, int> counts = await CountsAsync(dbContext, [first, second]);

        Assert.Equal(2, counts.Count);
        Assert.Equal(2, counts[first]);
        Assert.Equal(1, counts[second]);
    }

    /// <summary>Students outside the submitted set never appear — only submitted students are recounted (V-20).</summary>
    [Fact]
    public async Task PriorAbsenceCounts_OmitsStudentsOutsideTheSubmittedSet()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        Guid schoolId = Guid.NewGuid();
        Guid submitted = Guid.NewGuid();
        Guid omitted = Guid.NewGuid();

        await StudentAttendanceSeed.AddAsync(dbContext, submitted, schoolId, SubmittedDate.AddDays(-1));
        await StudentAttendanceSeed.AddAsync(dbContext, omitted, schoolId, SubmittedDate.AddDays(-1));

        Dictionary<Guid, int> counts = await CountsAsync(dbContext, [submitted]);

        Assert.Equal([submitted], counts.Keys);
    }

    private static Task<Dictionary<Guid, int>> CountsAsync(
        SparkrockRwcDbContext dbContext,
        IReadOnlyCollection<Guid> studentIds) =>
        AbsenceRecount
            .PriorAbsenceCounts(dbContext.StudentAttendances, studentIds, Year, SubmittedDate)
            .ToDictionaryAsync(count => count.StudentId, count => count.Count, CancellationToken.None);
}
