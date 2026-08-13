using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using domain.Attendance;
using domain.AttendanceCodes;
using domain.Schools;
using domain.Students;
using features.Attendance;
using features.integration.tests.Fakes;
using features.Paging;
using infra.persistence.postgre;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using NpgsqlTypes;

namespace features.integration.tests.Attendance;

/// <summary>
///     V-12's verification: the history filter is a sargable range on <c>attend_date</c>, and the
///     planner uses <c>ix_student_attendances_student_id_attend_date</c> for it.
/// </summary>
/// <remarks>
///     Integration-only by conventions §6, and not by preference. EF InMemory evaluates predicates in
///     LINQ-to-Objects, so the two ways of getting this wrong — a static domain call over a column, and
///     member access on a converted value (VC-31) — both pass at the handler tier and both throw on
///     Npgsql. Every assertion here is about the SQL that is generated or the plan that is chosen, and
///     none of them is also written in <c>features.tests</c>.
///     <para>
///         <b>What these tests cannot do.</b> There is no "before" side. The non-sargable forms throw
///         at translation rather than producing a slow plan, so nothing compares old against new. The
///         evidence is "the sargable form is generated and the planner uses it", not "it is faster than
///         the old one" — stated here because the divergence log's <c>Verified by</c> column will
///         otherwise read as the stronger claim (spec §5).
///     </para>
///     <para>
///         <c>SET enable_seqscan = off</c> is deliberately not used: it forces the plan the test claims
///         to observe and makes the assertion vacuous.
///     </para>
/// </remarks>
[Collection(IntegrationTestCollectionDefinition.Name)]
public sealed class StudentAttendanceHistoryPlanTests(PostgresContainerFixture fixture)
{
    private const string Index = "ix_student_attendances_student_id_attend_date";

    /// <summary>
    ///     The school year the assertions query. The seed spans the years either side of it too, so the
    ///     range predicate has rows to exclude rather than a table that happens to match entirely.
    /// </summary>
    private const int QueriedStartYear = 2025;

    private static readonly DateOnly RangeFrom = new(QueriedStartYear, 9, 1);

    private static readonly DateOnly RangeToExclusive = new(QueriedStartYear + 1, 9, 1);

    private static readonly SemaphoreSlim SeedGate = new(1, 1);

    private static bool _seeded;

    private static Guid _schoolId;

    private static Guid _studentId;

    private static Guid _attendanceCodeId;

    /// <summary>
    ///     <b>V-12's <c>Verified by</c>.</b> The plan reaches the rows through the composite index — an
    ///     equality on the leading column and a range on the second, which is the shape a B-tree serves
    ///     directly — and not by scanning the table.
    /// </summary>
    /// <remarks>
    ///     The absence of <c>Seq Scan on student_attendances</c> is asserted alongside the index name
    ///     because the two fail differently: an index mentioned somewhere in a plan that also scans the
    ///     table is not the plan this claims to observe. <c>school_terms</c> is scanned, legitimately —
    ///     it is a small reference table, and the assertion is deliberately about the large one.
    /// </remarks>
    [Fact]
    public async Task Explain_WhenFilteredByDateRange_UsesIndexOnStudentIdAndAttendDate()
    {
        await EnsureSeededAsync();

        await using SparkrockRwcDbContext dbContext = fixture.CreateDbContext();

        string plan = await ExplainAsync(Sql(dbContext));

        Assert.Contains(Index, plan, StringComparison.Ordinal);
        Assert.DoesNotContain("Seq Scan on student_attendances", plan, StringComparison.Ordinal);
    }

    /// <summary>
    ///     L-09 caught as a string comparison, and the fully deterministic half of V-12's evidence.
    /// </summary>
    /// <remarks>
    ///     <b>Deviation from spec §5, stated rather than quietly taken.</b> §5 asks that the whole of
    ///     <c>ToQueryString()</c> contain no <c>CASE</c>. It contains one, and it has to: §6 requires
    ///     every row to carry <c>origin</c>, which is
    ///     <c>a.SchoolId == student.SchoolId ? "currentSchool" : "otherSchool"</c> and translates to a
    ///     <c>CASE</c> in the <b>select list</b>. The two requirements cannot both be met literally, and
    ///     the alternative — projecting a bool and computing the string on the DTO — would be reshaping
    ///     the response contract to satisfy a string assertion.
    ///     <para>
    ///         So the <c>CASE</c> ban is applied where §5 says it is aimed — "if someone reintroduces a
    ///         computed year in the <c>WHERE</c> clause" — and the one permitted <c>CASE</c> is pinned
    ///         to the discriminator by asserting there is exactly one and that it is about
    ///         <c>school_id</c>. A second <c>CASE</c> anywhere, select list included, fails this.
    ///         <c>extract(</c> and <c>date_part</c> stay banned across the whole statement, because
    ///         nothing in this feature has any business computing a date part at all.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task Explain_WhenFilteredByDateRange_PredicateIsARangeOnAttendDate()
    {
        await EnsureSeededAsync();

        await using SparkrockRwcDbContext dbContext = fixture.CreateDbContext();

        string sql = Sql(dbContext);
        string where = WhereClause(sql);

        Assert.Contains("attend_date >=", where, StringComparison.Ordinal);
        Assert.Contains("attend_date <", where, StringComparison.Ordinal);

        Assert.DoesNotContain("CASE", where, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("extract(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("date_part", sql, StringComparison.OrdinalIgnoreCase);

        // The one CASE the statement is allowed, pinned to what it is for.
        Assert.Single(Regex.Matches(sql, @"\bCASE\b", RegexOptions.IgnoreCase));
        Assert.Matches(@"CASE\s+WHEN \w+\.school_id =", sql);
    }

    /// <summary>
    ///     The index is <b>partial</b> (<c>WHERE is_deleted = false</c>), so it is usable only when the
    ///     query's own predicate implies the index predicate. The reflective soft-delete filter supplies
    ///     that automatically (VC-13) and F08 writes nothing to get it — which is exactly why losing it
    ///     would be a silent plan regression rather than a failure.
    /// </summary>
    [Fact]
    public async Task Explain_WhenFilteredByDateRange_SoftDeleteFilterIsPresent()
    {
        await EnsureSeededAsync();

        await using SparkrockRwcDbContext dbContext = fixture.CreateDbContext();

        Assert.Matches(@"NOT \(\w+\.is_deleted\)", Sql(dbContext));
    }

    /// <summary>
    ///     Spec §9 acceptance criterion 8: the snapshot columns are read from
    ///     <c>student_attendances</c> and the code table is never joined. Structural rather than a
    ///     flag — it is what makes DEC-19's "a deactivated code still renders" true by construction.
    /// </summary>
    [Fact]
    public async Task Explain_WhenFilteredByDateRange_JoinsNoAttendanceCodeTable()
    {
        await EnsureSeededAsync();

        await using SparkrockRwcDbContext dbContext = fixture.CreateDbContext();

        Assert.DoesNotContain("attendance_codes", Sql(dbContext), StringComparison.Ordinal);
    }

    /// <summary>
    ///     Plain execution of the whole handler. Cheap, and the only tier where the VC-31 family of
    ///     mistakes can fail at all: on EF InMemory every one of them passes.
    /// </summary>
    [Fact]
    public async Task Query_WhenFilteredByDateRange_ExecutesAgainstPostgres()
    {
        await EnsureSeededAsync();

        await using SparkrockRwcDbContext dbContext = fixture.CreateDbContext();

        PagedResponse<GetStudentAttendanceHistory.Response> result =
            await new GetStudentAttendanceHistory.QueryHandler(
                    dbContext,
                    FakeCurrentUser.ScopedTo(_schoolId),
                    new FakeTimeProvider(ContainerDbContextFactory.DefaultNow))
                .Handle(
                    new GetStudentAttendanceHistory.Query
                    {
                        StudentId = _studentId,
                        SchoolYear = QueriedStartYear,
                        PageSize = PagingRules.MaxPageSize
                    },
                    CancellationToken.None);

        // One school year of daily rows, every one of them inside the half-open range.
        Assert.Equal(365, result.Page.TotalItems);
        Assert.All(
            result.Items,
            item => Assert.InRange(item.AttendDate, RangeFrom, RangeToExclusive.AddDays(-1)));
        Assert.All(
            result.Items,
            item => Assert.Equal(GetStudentAttendanceHistory.CurrentSchool, item.Origin));

        // Descending, so the first page starts at the last day inside the range.
        Assert.Equal(RangeToExclusive.AddDays(-1), result.Items[0].AttendDate);
    }

    // ------------------------------------------------------------------------------------ helpers

    private static string Sql(SparkrockRwcDbContext dbContext) =>
        GetStudentAttendanceHistory
            .HistoryQuery(dbContext, _studentId, _schoolId, RangeFrom, RangeToExclusive)
            .ToQueryString();

    /// <summary>
    ///     The statement's <c>WHERE</c> clause, so the ban on a computed year is asserted where §5 aims
    ///     it. The guard on the extraction is the assertion that it named the subject column: a split
    ///     that silently returned nothing would make every ban below it vacuous.
    /// </summary>
    private static string WhereClause(string sql)
    {
        int start = sql.LastIndexOf("WHERE ", StringComparison.Ordinal);

        Assert.True(start >= 0, "The generated statement has no WHERE clause, so it filters nothing.");

        int end = sql.IndexOf("ORDER BY", start, StringComparison.Ordinal);
        string where = end < 0 ? sql[start..] : sql[start..end];

        Assert.Contains("student_id", where, StringComparison.Ordinal);

        return where;
    }

    /// <summary>
    ///     One school, one student with three school years of daily rows, and enough other students
    ///     that a sequential scan is not the obvious plan whatever the predicate.
    /// </summary>
    /// <remarks>
    ///     The row count and the <c>ANALYZE</c> are both load-bearing. Postgres prefers a sequential
    ///     scan on a small table however selective the predicate is, so a plan assertion over ten rows
    ///     asserts nothing; and without statistics the planner works from defaults rather than from the
    ///     table it is about to read.
    ///     <para>
    ///         The decoy students exist because selectivity, not size, is what makes the index
    ///         attractive: with one student in the table, <c>student_id = @p</c> matches every row and
    ///         the planner would be right to scan. <c>(student_id, attend_date)</c> is unique, so one
    ///         student cannot hold more rows than the span has days — the table is made large by adding
    ///         students, never by repeating dates.
    ///     </para>
    ///     <para>
    ///         The database is shared across the collection, so this fixture owns its rows through fresh
    ///         <see cref="Guid" />s and asserts only about them. It never truncates.
    ///     </para>
    /// </remarks>
    private async Task EnsureSeededAsync()
    {
        await SeedGate.WaitAsync();

        try
        {
            if (_seeded)
                return;

            _schoolId = Guid.NewGuid();
            _studentId = Guid.NewGuid();
            _attendanceCodeId = Guid.NewGuid();

            await using (SparkrockRwcDbContext dbContext = fixture.CreateDbContext())
            {
                dbContext.Schools.Add(new School
                {
                    Id = _schoolId,
                    Name = "Plan Fixture School",
                    TimeZoneId = "America/Toronto"
                });

                dbContext.Students.Add(new Student
                {
                    Id = _studentId,
                    SchoolId = _schoolId,
                    FirstName = "Plan",
                    LastName = "Fixture"
                });

                // The rows carry a real foreign key to a code they never join to. The snapshot columns
                // are what the projection reads; the key is referential integrity only (F01d).
                dbContext.AttendanceCodes.Add(new AttendanceCode
                {
                    Id = _attendanceCodeId,
                    Value = "PL",
                    Description = "Plan fixture",
                    IsAbsent = true,
                    IsExcused = false
                });

                await dbContext.SaveChangesAsync();
            }

            DateOnly firstDay = new(QueriedStartYear - 1, 9, 1);
            DateOnly lastDayExclusive = new(QueriedStartYear + 2, 9, 1);

            await InsertAsync(_studentId, firstDay, lastDayExclusive);

            for (int decoy = 0; decoy < 30; decoy++)
            {
                Guid decoyId = Guid.NewGuid();

                await using SparkrockRwcDbContext dbContext = fixture.CreateDbContext();

                dbContext.Students.Add(new Student
                {
                    Id = decoyId,
                    SchoolId = _schoolId,
                    FirstName = "Decoy",
                    LastName = decoy.ToString(CultureInfo.InvariantCulture)
                });

                await dbContext.SaveChangesAsync();

                await InsertAsync(decoyId, firstDay, firstDay.AddDays(300));
            }

            await ExecuteAsync("ANALYZE student_attendances");

            _seeded = true;
        }
        finally
        {
            SeedGate.Release();
        }
    }

    private async Task InsertAsync(Guid studentId, DateOnly from, DateOnly toExclusive)
    {
        await using SparkrockRwcDbContext dbContext = fixture.CreateDbContext();

        for (DateOnly date = from; date < toExclusive; date = date.AddDays(1))
        {
            dbContext.StudentAttendances.Add(new StudentAttendance
            {
                Id = Guid.NewGuid(),
                StudentId = studentId,
                SchoolId = _schoolId,
                AttendDate = date,
                AttendanceCodeId = _attendanceCodeId,
                AttendCode = "A",
                AttendCodeDescription = "Absent",
                IsAbsent = true,
                IsExcused = false
            });
        }

        await dbContext.SaveChangesAsync();
    }

    /// <summary>
    ///     <c>ANALYZE</c>, around EF for the reason <c>DatabaseProbe</c> documents: conventions §7 bans
    ///     raw SQL through the DbContext, and routing a maintenance statement past the ban rather than
    ///     through it keeps the ban meaning what it says.
    /// </summary>
    private async Task ExecuteAsync(string sql)
    {
        await using NpgsqlConnection connection = new(fixture.ConnectionString);
        await connection.OpenAsync();

        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    ///     Runs <c>EXPLAIN</c> over the string EF generated, binding the parameters EF named.
    /// </summary>
    /// <remarks>
    ///     <c>ToQueryString()</c> keeps the placeholders in the statement and writes their values above
    ///     it as comments, so the statement cannot be executed as it stands. Rather than substitute
    ///     literals into it — which would make this a test of a string rewriter — each placeholder is
    ///     bound by the closed-over variable name EF embedded in it. That is what ties the observed plan
    ///     back to the exact query the handler runs, rather than to a hand-written equivalent.
    /// </remarks>
    private async Task<string> ExplainAsync(string queryString)
    {
        await using NpgsqlConnection connection = new(fixture.ConnectionString);
        await connection.OpenAsync();

        await using NpgsqlCommand command = new("EXPLAIN " + queryString, connection);

        MatchCollection placeholders = Regex.Matches(queryString, @"@__(?<name>[A-Za-z0-9]+)_\d+");

        Assert.NotEmpty(placeholders);

        foreach (Match match in placeholders)
        {
            string placeholder = match.Value[1..];

            if (!command.Parameters.Contains(placeholder))
                command.Parameters.Add(Bind(placeholder, match.Groups["name"].Value));
        }

        StringBuilder plan = new();

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
            plan.AppendLine(reader.GetString(0));

        return plan.ToString();
    }

    private static NpgsqlParameter Bind(string placeholder, string closedOverName) => closedOverName switch
    {
        "studentId" => new NpgsqlParameter(placeholder, NpgsqlDbType.Uuid) { Value = _studentId },
        "currentSchoolId" => new NpgsqlParameter(placeholder, NpgsqlDbType.Uuid) { Value = _schoolId },
        "rangeFrom" => new NpgsqlParameter(placeholder, NpgsqlDbType.Date) { Value = RangeFrom },
        "rangeToExclusive" => new NpgsqlParameter(placeholder, NpgsqlDbType.Date) { Value = RangeToExclusive },

        // Not a fallback to null: an unbound parameter would make EXPLAIN plan for a value the handler
        // never passes, and the plan assertion would then be about a different query.
        _ => throw new InvalidOperationException(
            $"The generated query carries an unrecognised parameter '{placeholder}'. Bind it here.")
    };
}
