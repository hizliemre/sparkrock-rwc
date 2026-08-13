using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using domain.Attendance;
using domain.AttendanceCodes;
using domain.Exceptions;
using domain.Schools;
using domain.Students;
using features.AttendanceSubmissions;
using features.integration.tests.Fakes;
using features.Paging;
using infra.persistence.postgre;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace features.integration.tests.AttendanceSubmissions;

/// <summary>
///     O-06's proof, and the platform assumption underneath it.
/// </summary>
/// <remarks>
///     <b>Integration-only, and not by preference.</b> Three things here are unavailable at the
///     handler tier:
///     <list type="number">
///         <item>
///             Whether Npgsql 8.0.11 translates <c>Guid.CompareTo</c> at all. No <c>VC-xx</c> covers
///             it and the whole cursor design rests on it (plan R-1). EF InMemory evaluates the
///             predicate in LINQ-to-Objects and so proves nothing (VC-31's precedent).
///         </item>
///         <item>
///             A genuine <c>submitted_at</c> tie. <c>timestamptz</c> stores microseconds; InMemory's
///             <c>DateTimeOffset</c> has no storage-precision behaviour, so a fixture written there
///             cannot demonstrate what happens when a page boundary falls <em>inside</em> a tie —
///             which is the only case a single-column cursor gets wrong.
///         </item>
///         <item>
///             That the planner uses <c>ix_submission_logs_school_id_submitted_at_id</c>.
///         </item>
///     </list>
///     <para>
///         The ordering assertions here are deliberately <b>properties</b> — "a paged traversal visits
///         the same rows, in the same order, as an unpaged one" — never a literal expected order. The
///         literal order over a tie depends on how the provider compares a <c>uuid</c>, and .NET and
///         Postgres disagree; asserting a literal here would either duplicate the handler tier or
///         encode Postgres' byte order into a test that is really about exactness.
///     </para>
///     <para>
///         <b>The table under test is expected to be empty in production.</b> Legacy wrote it and
///         never read it (L-01, legacy-analysis §5) and F12 imports none of it, so every row here is
///         one this fixture created. That is worth stating in the tier that would otherwise look like
///         it was validating migrated data.
///     </para>
/// </remarks>
[Collection(IntegrationTestCollectionDefinition.Name)]
public sealed class KeysetPagingTests(PostgresContainerFixture fixture)
{
    private const string Index = "ix_submission_logs_school_id_submitted_at_id";

    /// <summary>
    ///     A time whose sub-second component is a whole number of microseconds.
    /// </summary>
    /// <remarks>
    ///     Ticks are 100ns and <c>timestamptz</c> stores microseconds, so a value with a tick count
    ///     not divisible by ten is rounded on the way in and the round-trip assertions below would be
    ///     asserting the rounding rather than the format.
    /// </remarks>
    private static readonly DateTimeOffset MicrosecondPrecise =
        new DateTimeOffset(2026, 9, 14, 8, 31, 0, TimeSpan.Zero).AddTicks(1230);

    private static readonly DateOnly AttendDate = new(2026, 9, 14);

    private static readonly SemaphoreSlim SeedGate = new(1, 1);

    private static bool _seeded;

    /// <summary>The school the plan assertions query. Carries enough rows for the index to be worth using.</summary>
    private static Guid _planSchoolId;

    // ------------------------------------------------------------------- T11-01: the gate

    /// <summary>
    ///     <b>The spike.</b> Does the keyset predicate translate, and does it stay index-usable?
    /// </summary>
    /// <remarks>
    ///     The <c>::text</c> ban is the load-bearing half. <c>row.Id.ToString()</c> would also
    ///     "work" — it produces a running query — while rendering a <c>uuid</c>→<c>text</c> cast that
    ///     makes the composite index unusable, turning the keyset scan into a sequential one. That is
    ///     the whole thing keyset buys, and a test that only asserted "it executes" would pass through
    ///     it (plan R-1, option 2, explicitly rejected).
    /// </remarks>
    [Fact]
    public async Task Keyset_PredicateTranslatesToSql()
    {
        await EnsureSeededAsync();

        await using SparkrockRwcDbContext dbContext = fixture.CreateDbContext();

        string sql = CursorQuery(dbContext).ToQueryString();

        Assert.Contains("submitted_at <", sql, StringComparison.Ordinal);
        Assert.Contains("submitted_at =", sql, StringComparison.Ordinal);

        // The Guid comparison rendered as a plain uuid comparison, with no cast anywhere.
        Assert.Matches(@"\w+\.id < @", sql);
        Assert.DoesNotContain("::text", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("::character", sql, StringComparison.Ordinal);

        // And it executes. The translation question is answered by the string above; this is the
        // half that would catch a form that renders plausibly and then throws at execution.
        List<GetSchoolAttendanceSubmissions.Response> rows = await ToListAsync(CursorQuery(dbContext));

        Assert.NotNull(rows);
    }

    [Fact]
    public async Task Keyset_GeneratedSqlOrdersByBothColumns()
    {
        await EnsureSeededAsync();

        await using SparkrockRwcDbContext dbContext = fixture.CreateDbContext();

        string sql = CursorQuery(dbContext).ToQueryString();
        int orderBy = sql.LastIndexOf("ORDER BY", StringComparison.Ordinal);

        Assert.True(orderBy >= 0, "The generated statement has no ORDER BY, so its order is not total.");

        string order = sql[orderBy..];

        Assert.Matches(@"ORDER BY \w+\.submitted_at DESC, \w+\.id DESC", order);
    }

    // -------------------------------------------------- T11-09: the tie, the plan, the precision

    /// <summary>
    ///     <b>O-06's proof.</b> A page boundary that falls inside a <c>submitted_at</c> tie neither
    ///     skips a row nor repeats one.
    /// </summary>
    /// <remarks>
    ///     The fixture is built and then <em>interrogated</em> rather than assumed. Four logs are
    ///     written with two sharing an identical microsecond; the rows are read back unpaged, the
    ///     position of the tie in the server's own order is located, and the page size is chosen so
    ///     the boundary lands <b>between the two tied rows</b>. Without that step the boundary could
    ///     sit anywhere and the test would pass on a cursor that carried only the timestamp — which is
    ///     exactly the design O-06 rejected.
    ///     <para>
    ///         Every step of that reasoning is asserted: that the two rows really do share one
    ///         <c>submitted_at</c> after the database round trip, that the tie is adjacent in the
    ///         server order, and that the chosen page size splits it.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task Keyset_WhenTimestampsTieToTheMicrosecond_DoesNotSkipOrRepeat()
    {
        Guid schoolId = await SeedSchoolAsync();

        DateTimeOffset tie = MicrosecondPrecise;

        Guid[] ids = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];

        await using (SparkrockRwcDbContext writer = fixture.CreateDbContext())
        {
            // Two rows share `tie` exactly. The other two sit either side of it, so the tie is in the
            // middle of the ordered set rather than at an end where a boundary could not fall inside
            // it at all.
            writer.AttendanceSubmissionLogs.AddRange(
                Log(ids[0], schoolId, tie.AddSeconds(1)),
                Log(ids[1], schoolId, tie),
                Log(ids[2], schoolId, tie),
                Log(ids[3], schoolId, tie.AddSeconds(-1)));

            await writer.SaveChangesAsync(CancellationToken.None);
        }

        await using SparkrockRwcDbContext dbContext = fixture.CreateDbContext();

        IReadOnlyList<GetSchoolAttendanceSubmissions.Response> unpaged =
            (await HandleAsync(dbContext, schoolId, pageSize: 50)).Items;

        Assert.Equal(4, unpaged.Count);

        // The tie survived the round trip: Postgres stored both values as the same microsecond.
        Assert.Equal(2, unpaged.Count(item => item.SubmittedAt == unpaged[1].SubmittedAt));

        int tieStart = FirstTiedIndex(unpaged);

        // Fixture guard. If the tie were not adjacent in the server's order, no page size could put a
        // boundary inside it and everything below would be vacuous.
        Assert.InRange(tieStart, 0, unpaged.Count - 2);
        Assert.Equal(unpaged[tieStart].SubmittedAt, unpaged[tieStart + 1].SubmittedAt);

        // The boundary lands between the two tied rows: the first page ends on the first of them.
        int pageSize = tieStart + 1;

        Guid[] visited = await TraverseAsync(dbContext, schoolId, pageSize);

        Assert.Equal(unpaged.Select(item => item.Id), visited);
        Assert.Equal(4, visited.Distinct().Count());
    }

    /// <summary>
    ///     Acceptance criterion 12: the keyset scan reaches the rows through the composite index.
    /// </summary>
    /// <remarks>
    ///     Asserted on the index <b>name</b>, not on the node type — a bitmap scan and an index scan
    ///     are both acceptable, a sequential scan is not, so the absence of
    ///     <c>Seq Scan on attendance_submission_logs</c> is asserted alongside it. The two fail
    ///     differently: an index mentioned somewhere in a plan that also scans the table is not the
    ///     plan this claims to observe.
    ///     <para>
    ///         <c>SET enable_seqscan = off</c> is deliberately not used: it would force the plan the
    ///         test claims to observe and make the assertion vacuous.
    ///     </para>
    ///     <para>
    ///         <b>What this observes, and what it does not.</b> The plan is
    ///         <c>Index Scan Backward using ix_submission_logs_school_id_submitted_at_id</c> with
    ///         <c>Index Cond: (school_id = …)</c> and the cursor comparison in <c>Filter:</c> — not in
    ///         the index condition. So the ordering and the tenant predicate are served by the index
    ///         and nothing is sorted, but the scan still walks past the rows before the cursor rather
    ///         than seeking to it: deep paging is cheaper than <c>OFFSET</c> and is not constant-cost.
    ///         Making it an index condition needs a row-value comparison
    ///         (<c>(submitted_at, id) &lt; (@at, @id)</c>), which LINQ cannot express and which no
    ///         F11 document asks for. Acceptance criterion 12 asks that the scan use the index, and it
    ///         does; the stronger claim is recorded here rather than assumed.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task Keyset_ScanUsesTheCompositeIndex()
    {
        await EnsureSeededAsync();

        await using SparkrockRwcDbContext dbContext = fixture.CreateDbContext();

        string plan = await ExplainAsync(CursorQuery(dbContext).Take(51).ToQueryString());

        Assert.Contains(Index, plan, StringComparison.Ordinal);
        Assert.DoesNotContain("Seq Scan on attendance_submission_logs", plan, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A microsecond written into <c>submitted_at</c> comes back byte-identical, and survives the
    ///     cursor.
    /// </summary>
    /// <remarks>
    ///     A format string that truncates to milliseconds passes every unit test in
    ///     <c>SubmissionCursorTests</c> and then lands the cursor on the wrong side of a tie here. The
    ///     assertion is on ticks, because <c>DateTimeOffset</c> equality at second resolution would
    ///     hide exactly the digits that matter.
    /// </remarks>
    [Fact]
    public async Task List_MicrosecondPrecisionSurvivesTheRoundTrip()
    {
        Guid schoolId = await SeedSchoolAsync();
        Guid id = Guid.NewGuid();

        await using (SparkrockRwcDbContext writer = fixture.CreateDbContext())
        {
            writer.AttendanceSubmissionLogs.Add(Log(id, schoolId, MicrosecondPrecise));

            await writer.SaveChangesAsync(CancellationToken.None);
        }

        await using SparkrockRwcDbContext dbContext = fixture.CreateDbContext();

        GetSchoolAttendanceSubmissions.Response row =
            Assert.Single((await HandleAsync(dbContext, schoolId, pageSize: 50)).Items);

        Assert.Equal(MicrosecondPrecise.UtcTicks, row.SubmittedAt.UtcTicks);

        // The precision has to survive the encoding too, or the cursor's tie-break is comparing a
        // truncated value against a stored one.
        Assert.True(
            SubmissionCursor.TryDecode(new SubmissionCursor(row.SubmittedAt, row.Id).Encode(), out SubmissionCursor cursor));

        Assert.Equal(MicrosecondPrecise.UtcTicks, cursor.SubmittedAt.UtcTicks);
    }

    /// <summary>
    ///     The detail route's join against real foreign keys and the convention index on
    ///     <c>submission_id</c>, including the supersede arithmetic.
    /// </summary>
    [Fact]
    public async Task Detail_EntriesResolveThroughTheSubmissionIdForeignKey()
    {
        Guid schoolId = await SeedSchoolAsync();
        Guid firstSubmissionId = Guid.NewGuid();
        Guid secondSubmissionId = Guid.NewGuid();
        Guid codeId = Guid.NewGuid();
        Guid supersededRowId = Guid.NewGuid();
        Guid[] studentIds = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];

        await using (SparkrockRwcDbContext writer = fixture.CreateDbContext())
        {
            writer.AttendanceCodes.Add(new AttendanceCode
            {
                Id = codeId,
                Value = "K" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..3].ToUpperInvariant(),
                Description = "Keyset fixture",
                IsAbsent = true,
                IsExcused = false
            });

            for (int index = 0; index < studentIds.Length; index++)
            {
                writer.Students.Add(new Student
                {
                    Id = studentIds[index],
                    SchoolId = schoolId,
                    FirstName = "Entry",
                    LastName = FormattableString.Invariant($"Student{index}")
                });
            }

            writer.AttendanceSubmissionLogs.Add(Log(firstSubmissionId, schoolId, MicrosecondPrecise, recordCount: 3));
            writer.AttendanceSubmissionLogs.Add(
                Log(secondSubmissionId, schoolId, MicrosecondPrecise.AddMinutes(5), recordCount: 1));

            await writer.SaveChangesAsync(CancellationToken.None);

            for (int index = 0; index < studentIds.Length; index++)
            {
                writer.StudentAttendances.Add(new StudentAttendance
                {
                    Id = index == 2 ? supersededRowId : Guid.NewGuid(),
                    StudentId = studentIds[index],
                    SchoolId = schoolId,
                    AttendDate = AttendDate,
                    AttendanceCodeId = codeId,
                    SubmissionId = firstSubmissionId,
                    AttendCode = "A",
                    AttendCodeDescription = "Absent",
                    IsAbsent = true,
                    IsExcused = false
                });
            }

            await writer.SaveChangesAsync(CancellationToken.None);
        }

        // The partial upsert (V-20): a later submission rewrites one row and takes ownership of it.
        await using (SparkrockRwcDbContext updater = fixture.CreateDbContext())
        {
            StudentAttendance row = updater.StudentAttendances.Single(candidate => candidate.Id == supersededRowId);
            row.SubmissionId = secondSubmissionId;

            await updater.SaveChangesAsync(CancellationToken.None);
        }

        await using SparkrockRwcDbContext dbContext = fixture.CreateDbContext();

        GetAttendanceSubmissionById.Response response =
            await new GetAttendanceSubmissionById.QueryHandler(dbContext, FakeCurrentUser.ScopedTo(schoolId))
                .Handle(
                    new GetAttendanceSubmissionById.Query { SubmissionId = firstSubmissionId },
                    CancellationToken.None);

        Assert.Equal(schoolId, response.SchoolId);
        Assert.Equal(3, response.RecordCount);
        Assert.Equal(2, response.Entries.Count);
        Assert.Equal(1, response.SupersededCount);
        Assert.Equal(["Student0", "Student1"], response.Entries.Select(entry => entry.StudentLastName));
    }

    /// <summary>
    ///     The entries order is <b>total</b>: <c>last_name, first_name, student_id</c>.
    /// </summary>
    /// <remarks>
    ///     <b>Asserted on the ORDER BY, and it has to be.</b> A tie-break's observable effect is
    ///     whatever the provider happens to do with equal keys, so a handler-tier test that seeds two
    ///     students with identical names can return the right order with the tie-break removed —
    ///     verified: deleting <c>attendance.StudentId</c> from the <c>orderby</c> leaves
    ///     <c>Handle_OrdersEntriesByLastNameThenFirstNameThenStudentId</c> green, because EF InMemory's
    ///     join happened to yield the pair in the expected order anyway. Only the generated statement
    ///     says the order is total rather than merely observed to be.
    ///     <para>
    ///         It matters because the global <c>UseQuerySplittingBehavior(SplitQuery)</c> setting makes
    ///         a non-total order able to repeat one row and drop another (VC-27).
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task Detail_EntriesOrderIsTotal()
    {
        await EnsureSeededAsync();

        await using SparkrockRwcDbContext dbContext = fixture.CreateDbContext();

        string sql = GetAttendanceSubmissionById.EntriesQuery(dbContext, Guid.NewGuid()).ToQueryString();
        int orderBy = sql.LastIndexOf("ORDER BY", StringComparison.Ordinal);

        Assert.True(orderBy >= 0, "The entries statement has no ORDER BY, so its order is not total.");

        Assert.Matches(@"ORDER BY \w+\.last_name, \w+\.first_name, \w+\.(student_)?id", sql[orderBy..]);
    }

    /// <summary>
    ///     The tenant predicate against a real database, where an omission is not hidden by an empty
    ///     table.
    /// </summary>
    /// <remarks>
    ///     <c>WhereAuthorized</c>'s cost is that omitting it is silent (DEC-15's own remarks). The
    ///     shared container holds other tests' submissions, so a handler that dropped the scope
    ///     predicate would return one of them here rather than nothing.
    /// </remarks>
    [Fact]
    public async Task Detail_WhenSubmissionBelongsToAnotherSchool_IsNotFound()
    {
        Guid schoolId = await SeedSchoolAsync();
        Guid callersSchoolId = await SeedSchoolAsync();
        Guid submissionId = Guid.NewGuid();

        await using (SparkrockRwcDbContext writer = fixture.CreateDbContext())
        {
            writer.AttendanceSubmissionLogs.Add(Log(submissionId, schoolId, MicrosecondPrecise));

            await writer.SaveChangesAsync(CancellationToken.None);
        }

        await using SparkrockRwcDbContext dbContext = fixture.CreateDbContext();

        await Assert.ThrowsAsync<NotFoundException>(
            () => new GetAttendanceSubmissionById.QueryHandler(dbContext, FakeCurrentUser.ScopedTo(callersSchoolId))
                .Handle(new GetAttendanceSubmissionById.Query { SubmissionId = submissionId }, CancellationToken.None));
    }

    // ------------------------------------------------------------------------------------ helpers

    /// <summary>The index of the first row whose <c>SubmittedAt</c> equals its successor's, or -1.</summary>
    private static int FirstTiedIndex(IReadOnlyList<GetSchoolAttendanceSubmissions.Response> rows)
    {
        for (int index = 0; index < rows.Count - 1; index++)
        {
            if (rows[index].SubmittedAt == rows[index + 1].SubmittedAt)
                return index;
        }

        return -1;
    }

    private static async Task<Guid[]> TraverseAsync(SparkrockRwcDbContext dbContext, Guid schoolId, int pageSize)
    {
        List<Guid> visited = [];
        string? cursor = null;

        for (int page = 0; page < 20; page++)
        {
            KeysetResponse<GetSchoolAttendanceSubmissions.Response> result =
                await HandleAsync(dbContext, schoolId, pageSize, cursor);

            visited.AddRange(result.Items.Select(item => item.Id));

            if (!result.Page.HasMore)
                return [.. visited];

            cursor = result.Page.NextCursor;
        }

        Assert.Fail("The traversal did not terminate: nextCursor is not advancing.");

        return [];
    }

    private static async Task<KeysetResponse<GetSchoolAttendanceSubmissions.Response>> HandleAsync(
        SparkrockRwcDbContext dbContext,
        Guid schoolId,
        int pageSize,
        string? cursor = null) =>
        await new GetSchoolAttendanceSubmissions.QueryHandler(dbContext, FakeCurrentUser.ScopedTo(schoolId))
            .Handle(
                new GetSchoolAttendanceSubmissions.Query
                {
                    SchoolId = schoolId,
                    PageSize = pageSize,
                    Cursor = cursor
                },
                CancellationToken.None);

    /// <summary>
    ///     The shipped query with a cursor applied — never a hand-written equivalent, which could be
    ///     sargable while the shipped one is not.
    /// </summary>
    private static IQueryable<GetSchoolAttendanceSubmissions.Response> CursorQuery(
        SparkrockRwcDbContext dbContext) =>
        GetSchoolAttendanceSubmissions.LogQuery(
            dbContext,
            _planSchoolId,
            from: null,
            toExclusive: null,
            cursor: new SubmissionCursor(MicrosecondPrecise, Guid.Parse("88888888-8888-8888-8888-888888888888")));

    private static async Task<List<GetSchoolAttendanceSubmissions.Response>> ToListAsync(
        IQueryable<GetSchoolAttendanceSubmissions.Response> query) =>
        await query.Take(5).ToListAsync(CancellationToken.None);

    private static AttendanceSubmissionLog Log(
        Guid id,
        Guid schoolId,
        DateTimeOffset submittedAt,
        int recordCount = 1) => new()
    {
        Id = id,
        SchoolId = schoolId,
        AttendDate = AttendDate,
        SubmittedAt = submittedAt,
        RecordCount = recordCount,
        SubmittedBy = Guid.Parse("22222222-2222-2222-2222-222222222222")
    };

    private async Task<Guid> SeedSchoolAsync()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = fixture.CreateDbContext();

        dbContext.Schools.Add(new School
        {
            Id = schoolId,
            Name = "Keyset Fixture School",
            TimeZoneId = "America/Toronto"
        });

        await dbContext.SaveChangesAsync(CancellationToken.None);

        return schoolId;
    }

    /// <summary>
    ///     One school with a few hundred logs, and enough decoy schools that <c>school_id</c> is a
    ///     selective predicate.
    /// </summary>
    /// <remarks>
    ///     Both halves are load-bearing for the plan assertion. Postgres prefers a sequential scan on
    ///     a small table however selective the predicate, so a plan claim over ten rows claims
    ///     nothing; and with one school in the table <c>school_id = @p</c> matches everything and the
    ///     planner would be right to scan. <c>ANALYZE</c> matters for the same reason — without
    ///     statistics the planner works from defaults rather than from the table it is about to read.
    ///     <para>
    ///         The database is shared across the collection, so this owns its rows through fresh
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

            _planSchoolId = await SeedSchoolAsync();

            await InsertLogsAsync(_planSchoolId, 600);

            for (int decoy = 0; decoy < 20; decoy++)
                await InsertLogsAsync(await SeedSchoolAsync(), 200);

            await ExecuteAsync("ANALYZE attendance_submission_logs");

            _seeded = true;
        }
        finally
        {
            SeedGate.Release();
        }
    }

    private async Task InsertLogsAsync(Guid schoolId, int count)
    {
        await using SparkrockRwcDbContext dbContext = fixture.CreateDbContext();

        for (int index = 0; index < count; index++)
        {
            dbContext.AttendanceSubmissionLogs.Add(Log(
                Guid.NewGuid(), schoolId, MicrosecondPrecise.AddSeconds(index - (count / 2))));
        }

        await dbContext.SaveChangesAsync(CancellationToken.None);
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
    ///     <c>ToQueryString()</c> keeps the placeholders in the statement, so binding them by the
    ///     closed-over variable name EF embedded is what ties the observed plan back to the exact
    ///     query the handler runs rather than to a hand-written equivalent.
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
        "schoolId" => new NpgsqlParameter(placeholder, NpgsqlDbType.Uuid) { Value = _planSchoolId },
        "cursorAt" => new NpgsqlParameter(placeholder, NpgsqlDbType.TimestampTz) { Value = MicrosecondPrecise },
        "cursorId" => new NpgsqlParameter(placeholder, NpgsqlDbType.Uuid)
        {
            Value = Guid.Parse("88888888-8888-8888-8888-888888888888")
        },
        "p" => new NpgsqlParameter(placeholder, NpgsqlDbType.Integer) { Value = 51 },

        // Not a fallback to null: an unbound parameter would make EXPLAIN plan for a value the
        // handler never passes, and the plan assertion would then be about a different query.
        _ => throw new InvalidOperationException(
            $"The generated query carries an unrecognised parameter '{placeholder}'. Bind it here.")
    };
}
