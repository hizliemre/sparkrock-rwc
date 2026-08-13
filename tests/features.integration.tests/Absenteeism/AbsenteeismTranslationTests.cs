using domain.Attendance;
using domain.Schools;
using domain.Students;
using domain.ValueObjects;
using features.Absenteeism;
using infra.persistence.postgre;
using Microsoft.EntityFrameworkCore;

namespace features.integration.tests.Absenteeism;

/// <summary>
///     The VC-31 gate. <b>This is the only tier at which F09's central mistake can fail.</b>
/// </summary>
/// <remarks>
///     <c>StudentAttendanceSummary.SchoolYearStart</c> is a value-converted property. VC-31: the whole
///     value may be compared in a predicate, and <c>.StartYear</c> may be read only in a projection.
///     The illegal form — <c>s.SchoolYearStart.StartYear == 2026</c> in a <c>Where</c> — compiles,
///     and EF InMemory evaluates predicates in LINQ-to-Objects, so it returns the right rows and
///     passes every handler-tier test before throwing <see cref="InvalidOperationException" /> on
///     Npgsql and surfacing as a 500 <c>SYSTEM.UNEXPECTED</c> on a graded-minimum endpoint.
///     <para>
///         Design §5's F01f edge list — F03, F04, F08, F10 — omits F09. On the reasoning above that
///         omission is wrong, and F09 treats F01f as a <i>blocks-merge</i> edge. Reported to the
///         design owner rather than corrected here.
///     </para>
///     <para>
///         Every assertion runs over the slices' own <c>IQueryable</c> builders
///         (<see cref="GetStudentAbsenteeism.SummaryQuery" />,
///         <see cref="GetSchoolAbsenteeism.WorklistQuery" />) rather than a hand-written equivalent —
///         a hand-written query is a test of the test. Nothing behavioural is re-asserted here;
///         conventions §6 forbids duplicating a tier.
///     </para>
///     <para>
///         The database is shared across the collection: this class allocates fresh
///         <see cref="Guid" />s and asserts only about its own rows.
///     </para>
/// </remarks>
[Collection(IntegrationTestCollectionDefinition.Name)]
public sealed class AbsenteeismTranslationTests(PostgresContainerFixture fixture)
{
    private const int SeededStartYear = 2031;

    /// <summary>
    ///     The whole point. Building the handler's query and executing it against real Postgres is
    ///     the one place VC-31's failure mode is reachable.
    /// </summary>
    [Fact]
    public async Task Query_WhenFilteredBySchoolYear_ExecutesAgainstPostgres()
    {
        await using SparkrockRwcDbContext dbContext = fixture.CreateDbContext();
        Seeded seeded = await SeedAsync(dbContext);

        GetStudentAbsenteeism.SummaryRow? row = await GetStudentAbsenteeism
            .SummaryQuery(dbContext, seeded.StudentId, SchoolYear.FromStartYear(SeededStartYear))
            .FirstOrDefaultAsync(CancellationToken.None);

        Assert.NotNull(row);
        Assert.Equal(SeededStartYear, row.StartYear);
    }

    /// <summary>
    ///     VC-31's legal predicate: a whole-value comparison against a parameter, with no expression
    ///     wrapped around the column.
    /// </summary>
    [Fact]
    public async Task Query_WhenFilteredBySchoolYear_ComparesTheWholeValue()
    {
        await using SparkrockRwcDbContext dbContext = fixture.CreateDbContext();
        Seeded seeded = await SeedAsync(dbContext);

        string sql = GetStudentAbsenteeism
            .SummaryQuery(dbContext, seeded.StudentId, SchoolYear.FromStartYear(SeededStartYear))
            .ToQueryString();

        string where = Section(sql, "WHERE");

        Assert.Contains("school_year_start = @", where, StringComparison.Ordinal);

        // The illegal form cannot produce SQL at all, but a "helpful" later rewrite that wraps the
        // column — a cast, an extract, an arithmetic adjustment — would, and it would be unsargable
        // against ix_summaries_student_id_school_year_start.
        foreach (string wrapper in new[] { "(school_year_start", "school_year_start +", "school_year_start -", "CAST(s.school_year_start" })
            Assert.DoesNotContain(wrapper, where, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     VC-31's legal half, asserted so a later "simplification" that materialises the summary and
    ///     computes the year in memory is caught. The plan names this as a likely review suggestion.
    /// </summary>
    [Fact]
    public async Task Query_WhenProjectingStartYear_SelectsTheColumn()
    {
        await using SparkrockRwcDbContext dbContext = fixture.CreateDbContext();
        Seeded seeded = await SeedAsync(dbContext);

        string sql = GetStudentAbsenteeism
            .SummaryQuery(dbContext, seeded.StudentId, SchoolYear.FromStartYear(SeededStartYear))
            .ToQueryString();

        Assert.Contains("school_year_start", Section(sql, "SELECT"), StringComparison.Ordinal);
    }

    /// <summary>
    ///     The list has more moving parts than the single read — a left join, a coalesced
    ///     <c>ORDER BY</c> and the <c>chronicOnly</c> predicate — and is the one where the coalesce
    ///     could fail to translate.
    /// </summary>
    [Fact]
    public async Task Query_SchoolWideListExecutesAgainstPostgres()
    {
        await using SparkrockRwcDbContext dbContext = fixture.CreateDbContext();
        Seeded seeded = await SeedAsync(dbContext);

        List<GetSchoolAbsenteeism.Row> rows = await GetSchoolAbsenteeism
            .WorklistQuery(
                dbContext,
                seeded.SchoolId,
                SchoolYear.FromStartYear(SeededStartYear),
                threshold: 10,
                chronicOnly: true,
                includeInactive: false)
            .ToListAsync(CancellationToken.None);

        Assert.Equal(seeded.StudentId, Assert.Single(rows).StudentId);
    }

    /// <summary>
    ///     <b>The <c>NULLS FIRST</c> trap, asserted where it is real.</b> A left join leaves the
    ///     summary's <c>total_absences</c> null, and Postgres sorts nulls <i>first</i> under
    ///     <c>ORDER BY … DESC</c> — which would put the students with no absences at the top of a
    ///     worst-first worklist. Ordering over the coalesced projection eliminates the case.
    /// </summary>
    /// <remarks>
    ///     This assertion is impossible at the handler tier: EF InMemory sorts a nullable
    ///     <c>int?</c> descending with nulls last, so dropping the coalesce still passes there.
    /// </remarks>
    [Fact]
    public async Task Query_SchoolWideListSortsStudentsWithNoSummaryToTheBottom()
    {
        await using SparkrockRwcDbContext dbContext = fixture.CreateDbContext();
        Seeded seeded = await SeedAsync(dbContext);

        Student withoutSummary = new()
        {
            Id = Guid.NewGuid(),
            SchoolId = seeded.SchoolId,
            FirstName = "Nil",
            LastName = "Aaaa"
        };

        dbContext.Students.Add(withoutSummary);
        await dbContext.SaveChangesAsync(CancellationToken.None);

        List<GetSchoolAbsenteeism.Row> rows = await GetSchoolAbsenteeism
            .WorklistQuery(
                dbContext,
                seeded.SchoolId,
                SchoolYear.FromStartYear(SeededStartYear),
                threshold: 10,
                chronicOnly: false,
                includeInactive: false)
            .ToListAsync(CancellationToken.None);

        Assert.Equal([seeded.StudentId, withoutSummary.Id], rows.Select(row => row.StudentId).ToArray());
        Assert.Equal(0, rows[1].TotalAbsences);
    }

    /// <summary>
    ///     The generated <c>ORDER BY</c> runs over the coalesced expression, not the bare nullable
    ///     column. The row-order assertion above would still pass on a single-row page; this pins the
    ///     SQL that makes it hold for every page.
    /// </summary>
    [Fact]
    public async Task Query_SchoolWideListOrdersOverTheCoalescedExpression()
    {
        await using SparkrockRwcDbContext dbContext = fixture.CreateDbContext();
        Seeded seeded = await SeedAsync(dbContext);

        string sql = GetSchoolAbsenteeism
            .WorklistQuery(
                dbContext,
                seeded.SchoolId,
                SchoolYear.FromStartYear(SeededStartYear),
                threshold: 10,
                chronicOnly: false,
                includeInactive: false)
            .ToQueryString();

        string orderBy = Section(sql, "ORDER BY");

        // Npgsql renders the coalesce as `CASE WHEN t.id IS NULL THEN 0 ELSE t.total_absences END`.
        // The substitution to *zero* is the assertion, not merely the presence of a CASE: an ORDER BY
        // over any expression that can still evaluate to NULL reinstates the trap.
        Assert.True(
            orderBy.Contains("THEN 0", StringComparison.OrdinalIgnoreCase)
            || orderBy.Contains(", 0)", StringComparison.Ordinal),
            $"ORDER BY must substitute zero for a missing summary, not sort a nullable column. Got: {orderBy}");
    }

    /// <summary>
    ///     The tail of <paramref name="sql" /> from <paramref name="keyword" /> to the next top-level
    ///     clause, so an assertion about the <c>WHERE</c> cannot be satisfied by text in the
    ///     <c>SELECT</c>.
    /// </summary>
    private static string Section(string sql, string keyword)
    {
        int start = sql.IndexOf(keyword, StringComparison.Ordinal);

        Assert.True(start >= 0, $"The generated SQL has no {keyword} clause:{Environment.NewLine}{sql}");

        int end = sql.Length;

        foreach (string next in new[] { "FROM", "LEFT JOIN", "INNER JOIN", "WHERE", "GROUP BY", "ORDER BY", "LIMIT" })
        {
            int candidate = sql.IndexOf(next, start + keyword.Length, StringComparison.Ordinal);

            if (candidate >= 0 && candidate < end)
                end = candidate;
        }

        return sql[start..end];
    }

    private static async Task<Seeded> SeedAsync(SparkrockRwcDbContext dbContext)
    {
        School school = new()
        {
            Id = Guid.NewGuid(),
            Name = $"Translation {Guid.NewGuid()}",
            TimeZoneId = "America/Toronto",
            AbsenceAlertThreshold = 10
        };

        Student student = new()
        {
            Id = Guid.NewGuid(),
            SchoolId = school.Id,
            FirstName = "Vc",
            LastName = "Thirtyone"
        };

        StudentAttendanceSummary summary = new()
        {
            Id = Guid.NewGuid(),
            StudentId = student.Id,
            SchoolId = school.Id,
            SchoolYearStart = SchoolYear.FromStartYear(SeededStartYear),
            TotalAbsences = 14
        };

        dbContext.Schools.Add(school);
        dbContext.Students.Add(student);
        dbContext.StudentAttendanceSummaries.Add(summary);
        await dbContext.SaveChangesAsync(CancellationToken.None);

        return new Seeded(school.Id, student.Id);
    }

    private sealed record Seeded(Guid SchoolId, Guid StudentId);
}
