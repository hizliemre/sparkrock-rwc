using System.Reflection;
using domain.Attendance;
using domain.Exceptions;
using features.AttendanceSubmissions;
using features.Paging;
using features.tests.Fakes;
using FluentValidation.Results;
using infra.persistence.postgre;

namespace features.tests.AttendanceSubmissions;

/// <summary>
///     <c>GET /schools/{schoolId}/attendance-submissions</c> — the keyset-paged submission log.
/// </summary>
public sealed class GetSchoolAttendanceSubmissionsValidatorTests
{
    private static readonly Guid SchoolId = Guid.NewGuid();

    [Fact]
    public void Validate_WhenPageSizeExceedsMax_Fails()
    {
        ValidationFailure failure = Assert.Single(
            Validate(new GetSchoolAttendanceSubmissions.Query
            {
                SchoolId = SchoolId,
                PageSize = PagingRules.MaxPageSize + 1
            }).Errors);

        Assert.Equal(ErrorCodes.Validation.PageSizeExceeded, failure.ErrorCode);

        // The property name is the query key. api/Errors/ViolationSource resolves `source` by
        // matching the camel-cased root of the path against the request's query keys, so a rename on
        // either side silently degrades "source": "query" into whatever the fallback answers.
        Assert.Equal(nameof(GetSchoolAttendanceSubmissions.Query.PageSize), failure.PropertyName);
    }

    /// <summary>
    ///     <c>?page=</c> is not accepted on a keyset route, and is rejected rather than ignored.
    /// </summary>
    /// <remarks>
    ///     Ignoring it serves page 1 to a client that believes it is on page 7 — a plausible result
    ///     set and no signal at all.
    /// </remarks>
    [Fact]
    public void Validate_WhenPageIsSupplied_Fails()
    {
        ValidationFailure failure = Assert.Single(
            Validate(new GetSchoolAttendanceSubmissions.Query { SchoolId = SchoolId, Page = 1 }).Errors);

        Assert.Equal(ErrorCodes.Validation.Failed, failure.ErrorCode);
        Assert.Equal(nameof(GetSchoolAttendanceSubmissions.Query.Page), failure.PropertyName);
    }

    /// <summary>
    ///     An inverted range returns an empty page, which reads as "nothing was submitted" rather than
    ///     "your request was wrong".
    /// </summary>
    [Fact]
    public void Validate_WhenToExclusiveIsNotAfterFrom_Fails()
    {
        ValidationFailure failure = Assert.Single(
            Validate(new GetSchoolAttendanceSubmissions.Query
            {
                SchoolId = SchoolId,
                From = new DateOnly(2026, 9, 20),
                ToExclusive = new DateOnly(2026, 9, 14)
            }).Errors);

        Assert.Equal(ErrorCodes.Validation.Failed, failure.ErrorCode);
    }

    /// <summary>Half-open, so an equal pair is an empty range — a request that cannot have been meant.</summary>
    [Fact]
    public void Validate_WhenToExclusiveEqualsFrom_Fails()
    {
        Assert.False(
            Validate(new GetSchoolAttendanceSubmissions.Query
            {
                SchoolId = SchoolId,
                From = SubmissionLogSeed.BaseDate,
                ToExclusive = SubmissionLogSeed.BaseDate
            }).IsValid);
    }

    [Fact]
    public void Validate_WhenRangeIsAbsent_Succeeds()
    {
        Assert.True(Validate(new GetSchoolAttendanceSubmissions.Query { SchoolId = SchoolId }).IsValid);
    }

    /// <summary>
    ///     Each bound is independently optional (spec §5), unlike F08 where a half-open range is an
    ///     unbounded read. Keyset paging bounds the read here whatever the filter says.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Validate_WhenOnlyOneBoundIsSupplied_Succeeds(bool hasFrom, bool hasToExclusive)
    {
        Assert.True(
            Validate(new GetSchoolAttendanceSubmissions.Query
            {
                SchoolId = SchoolId,
                From = hasFrom ? SubmissionLogSeed.BaseDate : null,
                ToExclusive = hasToExclusive ? SubmissionLogSeed.BaseDate.AddDays(7) : null
            }).IsValid);
    }

    /// <summary>
    ///     O-06: an undecodable cursor is a 400, <b>never</b> silently ignored.
    /// </summary>
    /// <remarks>
    ///     Ignoring it serves page 1, so a client following <c>nextCursor</c> in a loop is handed the
    ///     beginning of the log again and never terminates — a hang rather than an error.
    /// </remarks>
    [Theory]
    [MemberData(nameof(SubmissionCursorTests.Malformed), MemberType = typeof(SubmissionCursorTests))]
    public void Validate_WhenCursorIsMalformed_Fails(string? cursor)
    {
        // `null` is the absent cursor and is valid; the theory shares SubmissionCursorTests' data so
        // the two can never drift, and this is the one row that means something different here.
        if (cursor is null)
            return;

        ValidationFailure failure = Assert.Single(
            Validate(new GetSchoolAttendanceSubmissions.Query { SchoolId = SchoolId, Cursor = cursor }).Errors);

        Assert.Equal(ErrorCodes.Validation.InvalidCursor, failure.ErrorCode);
        Assert.Equal(nameof(GetSchoolAttendanceSubmissions.Query.Cursor), failure.PropertyName);
    }

    [Fact]
    public void Validate_WhenCursorIsWellFormed_Succeeds()
    {
        string cursor = new SubmissionCursor(SubmissionLogSeed.BaseInstant, Guid.NewGuid()).Encode();

        Assert.True(Validate(new GetSchoolAttendanceSubmissions.Query
        {
            SchoolId = SchoolId,
            Cursor = cursor
        }).IsValid);
    }

    [Fact]
    public void Validate_WhenCursorIsAbsent_Succeeds()
    {
        Assert.True(Validate(new GetSchoolAttendanceSubmissions.Query { SchoolId = SchoolId, Cursor = null }).IsValid);
    }

    private static ValidationResult Validate(GetSchoolAttendanceSubmissions.Query query) =>
        new GetSchoolAttendanceSubmissions.QueryValidator().Validate(query);
}

/// <summary>
///     The handler: scope, range, ordering, the over-fetch boundary and the cursor.
/// </summary>
/// <remarks>
///     <b>Every ordering assertion in this file that involves <see cref="Guid" /> is handler-tier
///     only.</b> EF InMemory sorts with <c>Comparer&lt;Guid&gt;.Default</c>, which compares
///     <c>Data1</c> as a signed <c>int</c> and then the remaining fields; Postgres compares a
///     <c>uuid</c> as sixteen big-endian bytes. The two disagree, and F11 is safe only because no
///     <see cref="Guid" /> is ever ordered or compared in C#. The provider-agnostic property — a paged
///     traversal visits the same rows in the same order as an unpaged one — is asserted here <em>and</em>
///     against a real <c>timestamptz</c> in <c>features.integration.tests</c>, because only the latter
///     can produce a genuine microsecond tie.
/// </remarks>
public sealed class GetSchoolAttendanceSubmissionsHandlerTests
{
    /// <summary>
    ///     Opening day, and for a long time after it.
    /// </summary>
    /// <remarks>
    ///     Legacy wrote <c>AttendanceSubmissionLog</c> and never read it; the insert sits after
    ///     <c>CLOSE cur</c> so it was unreachable (L-01, legacy-analysis §5), and F12 imports none of
    ///     it. <b>An empty list is the correct answer, not a defect</b> — and specifically not a 404,
    ///     which would make "this school has never submitted" indistinguishable from "no such school".
    /// </remarks>
    [Fact]
    public async Task Handle_WhenNoSubmissionsExist_ReturnsEmptyEnvelope()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        KeysetResponse<GetSchoolAttendanceSubmissions.Response> result =
            await HandleAsync(dbContext, schoolId, schoolId);

        Assert.Empty(result.Items);
        Assert.False(result.Page.HasMore);
        Assert.Null(result.Page.NextCursor);
        Assert.Equal(PagingRules.DefaultPageSize, result.Page.Size);
    }

    [Fact]
    public async Task Handle_ReturnsOnlyTheRouteSchoolsSubmissions()
    {
        Guid schoolId = Guid.NewGuid();
        Guid otherSchoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        Guid mine = Guid.NewGuid();

        await SubmissionLogSeed.SaveAsync(
            dbContext,
            SubmissionLogSeed.Log(mine, schoolId, SubmissionLogSeed.BaseDate, SubmissionLogSeed.BaseInstant),
            SubmissionLogSeed.Log(
                Guid.NewGuid(), otherSchoolId, SubmissionLogSeed.BaseDate, SubmissionLogSeed.BaseInstant));

        KeysetResponse<GetSchoolAttendanceSubmissions.Response> result = await HandleAsync(
            dbContext, schoolId, schoolId, otherSchoolId);

        Assert.Equal([mine], result.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task Handle_WhenSchoolOutsideScope_ThrowsNotFound()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => HandleAsync(dbContext, Guid.NewGuid(), Guid.NewGuid()));

        Assert.Equal(ErrorCodes.Attendance.SubmissionNotFound, exception.ErrorCode);
    }

    /// <summary>
    ///     An <b>unknown</b> school that is nonetheless in scope returns an empty page, not a 404.
    /// </summary>
    /// <remarks>
    ///     <b>Named differently from tasks.md's <c>Handle_WhenSchoolUnknown_ThrowsTheSameNotFound</c>,
    ///     deliberately.</b> Spec §5's pipeline is <c>EnsureAuthorized</c> then a filter — it never
    ///     loads the school row, so "unknown" is not a state this handler can observe. What tasks.md
    ///     describes is an unknown school that is also out of scope, which is
    ///     <see cref="Handle_UnknownAndOutOfScopeProduceIdenticalExceptions" />. This test pins the
    ///     other half so the gap is stated rather than left as an assumption: adding a school-exists
    ///     lookup here would turn every opening-day request into a 404 for a school that simply has
    ///     not submitted yet.
    /// </remarks>
    [Fact]
    public async Task Handle_WhenSchoolIsInScopeButUnknown_ReturnsEmptyEnvelope()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        KeysetResponse<GetSchoolAttendanceSubmissions.Response> result =
            await HandleAsync(dbContext, schoolId, schoolId);

        Assert.Empty(result.Items);
    }

    /// <summary>
    ///     Conventions §2's existence oracle: an unauthorised school and an unknown school produce
    ///     byte-identical failures.
    /// </summary>
    /// <remarks>
    ///     Both are out of scope for this identity, so both take the <c>EnsureAuthorized</c> branch and
    ///     <see cref="NotFoundException" /> carries no message — the payloads are identical by
    ///     construction rather than by call-site discipline.
    /// </remarks>
    [Fact]
    public async Task Handle_UnknownAndOutOfScopeProduceIdenticalExceptions()
    {
        Guid inScopeSchoolId = Guid.NewGuid();
        Guid realButUnauthorisedSchoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        dbContext.Schools.Add(SubmissionLogSeed.School(realButUnauthorisedSchoolId));
        await dbContext.SaveChangesAsync(CancellationToken.None);

        NotFoundException unknown = await Assert.ThrowsAsync<NotFoundException>(
            () => HandleAsync(dbContext, Guid.NewGuid(), inScopeSchoolId));

        NotFoundException unauthorised = await Assert.ThrowsAsync<NotFoundException>(
            () => HandleAsync(dbContext, realButUnauthorisedSchoolId, inScopeSchoolId));

        Assert.Equal(unknown.ErrorCode, unauthorised.ErrorCode);
        Assert.Equal(unknown.Message, unauthorised.Message);
        Assert.Equal(unknown.GetType(), unauthorised.GetType());
    }

    [Fact]
    public async Task Handle_OrdersBySubmittedAtDescending()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        Guid oldest = Guid.NewGuid();
        Guid middle = Guid.NewGuid();
        Guid newest = Guid.NewGuid();

        await SubmissionLogSeed.SaveAsync(
            dbContext,
            SubmissionLogSeed.Log(oldest, schoolId, SubmissionLogSeed.BaseDate, SubmissionLogSeed.BaseInstant),
            SubmissionLogSeed.Log(
                newest, schoolId, SubmissionLogSeed.BaseDate, SubmissionLogSeed.BaseInstant.AddMinutes(2)),
            SubmissionLogSeed.Log(
                middle, schoolId, SubmissionLogSeed.BaseDate, SubmissionLogSeed.BaseInstant.AddMinutes(1)));

        KeysetResponse<GetSchoolAttendanceSubmissions.Response> result =
            await HandleAsync(dbContext, schoolId, schoolId);

        Assert.Equal([newest, middle, oldest], result.Items.Select(item => item.Id));
    }

    /// <summary>
    ///     The tie-break, at the tier that can express it deterministically.
    /// </summary>
    /// <remarks>
    ///     Five rows sharing one <c>SubmittedAt</c>, inserted in ascending id order. Without
    ///     <c>ThenByDescending(id)</c> the provider's stable sort returns them in insertion order —
    ///     ascending — so the assertion fails. The expected order is <c>Comparer&lt;Guid&gt;.Default</c>
    ///     descending, which is EF InMemory's and <b>not</b> Postgres': the same claim against a real
    ///     <c>uuid</c> column is <c>KeysetPagingTests</c>', expressed as a property rather than as a
    ///     literal order.
    /// </remarks>
    [Fact]
    public async Task Handle_WhenSubmittedAtTies_OrdersByIdDescending()
    {
        Guid schoolId = Guid.NewGuid();
        Guid[] ascendingIds = SubmissionLogSeed.AscendingIds(5);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        await SubmissionLogSeed.SaveAsync(
            dbContext,
            [
                .. ascendingIds.Select(id => SubmissionLogSeed.Log(
                    id, schoolId, SubmissionLogSeed.BaseDate, SubmissionLogSeed.BaseInstant))
            ]);

        KeysetResponse<GetSchoolAttendanceSubmissions.Response> result =
            await HandleAsync(dbContext, schoolId, schoolId);

        Assert.Equal(ascendingIds.Reverse(), result.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task Handle_FromIsInclusive()
    {
        Guid schoolId = Guid.NewGuid();
        Guid onTheBoundary = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        await SubmissionLogSeed.SaveAsync(
            dbContext,
            SubmissionLogSeed.Log(
                onTheBoundary, schoolId, SubmissionLogSeed.BaseDate, SubmissionLogSeed.BaseInstant),
            SubmissionLogSeed.Log(
                Guid.NewGuid(),
                schoolId,
                SubmissionLogSeed.BaseDate.AddDays(-1),
                SubmissionLogSeed.BaseInstant.AddMinutes(1)));

        KeysetResponse<GetSchoolAttendanceSubmissions.Response> result = await HandleAsync(
            dbContext, schoolId, schoolId, from: SubmissionLogSeed.BaseDate);

        Assert.Equal([onTheBoundary], result.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task Handle_ToExclusiveIsExclusive()
    {
        Guid schoolId = Guid.NewGuid();
        Guid theDayBefore = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        await SubmissionLogSeed.SaveAsync(
            dbContext,
            SubmissionLogSeed.Log(
                Guid.NewGuid(), schoolId, SubmissionLogSeed.BaseDate, SubmissionLogSeed.BaseInstant),
            SubmissionLogSeed.Log(
                theDayBefore,
                schoolId,
                SubmissionLogSeed.BaseDate.AddDays(-1),
                SubmissionLogSeed.BaseInstant.AddMinutes(1)));

        KeysetResponse<GetSchoolAttendanceSubmissions.Response> result = await HandleAsync(
            dbContext, schoolId, schoolId, toExclusive: SubmissionLogSeed.BaseDate);

        Assert.Equal([theDayBefore], result.Items.Select(item => item.Id));
    }

    /// <summary>
    ///     The range filters <c>AttendDate</c>; the order and the cursor use <c>SubmittedAt</c>. They
    ///     are deliberately different columns (spec §5), and this is the test that says so.
    /// </summary>
    /// <remarks>
    ///     <c>AttendDate</c> is the question a user asks — "was attendance submitted for the week of 14
    ///     September?" — and it repeats freely, because a school can re-submit the same date any number
    ///     of times (V-20). <c>SubmittedAt</c> is the only column that gives an append-only log a
    ///     stable total order.
    /// </remarks>
    [Fact]
    public async Task Handle_RangeFiltersAttendDateNotSubmittedAt()
    {
        Guid schoolId = Guid.NewGuid();
        Guid lateCorrectionOfAnOldDate = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        // Submitted inside the window, but for a date well outside it: a clerk correcting last month.
        await SubmissionLogSeed.SaveAsync(
            dbContext,
            SubmissionLogSeed.Log(
                lateCorrectionOfAnOldDate,
                schoolId,
                SubmissionLogSeed.BaseDate.AddDays(-30),
                SubmissionLogSeed.BaseInstant));

        KeysetResponse<GetSchoolAttendanceSubmissions.Response> result = await HandleAsync(
            dbContext,
            schoolId,
            schoolId,
            from: SubmissionLogSeed.BaseDate,
            toExclusive: SubmissionLogSeed.BaseDate.AddDays(1));

        Assert.Empty(result.Items);
    }

    /// <summary>
    ///     Plan R-3's boundary: exactly <c>pageSize</c> rows remain, so the over-fetch of
    ///     <c>size + 1</c> returns <c>size</c> and <c>hasMore</c> must be false.
    /// </summary>
    /// <remarks>
    ///     The off-by-one here is the one that looks right in every other case. <c>rows.Count &gt;= size</c>
    ///     rather than <c>&gt;</c> yields a <c>nextCursor</c> for a page that has no successor, and the
    ///     client's next request returns an empty page — plausible, and one wasted round trip per
    ///     traversal forever.
    /// </remarks>
    [Fact]
    public async Task Handle_WhenExactlyPageSizeRowsExist_HasMoreIsFalse()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        await SeedAsync(dbContext, schoolId, 3);

        KeysetResponse<GetSchoolAttendanceSubmissions.Response> result =
            await HandleAsync(dbContext, schoolId, schoolId, pageSize: 3);

        Assert.Equal(3, result.Items.Count);
        Assert.False(result.Page.HasMore);
        Assert.Null(result.Page.NextCursor);
    }

    [Fact]
    public async Task Handle_WhenMoreThanPageSizeRowsExist_HasMoreIsTrueAndCursorIsPresent()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        await SeedAsync(dbContext, schoolId, 4);

        KeysetResponse<GetSchoolAttendanceSubmissions.Response> result =
            await HandleAsync(dbContext, schoolId, schoolId, pageSize: 3);

        Assert.Equal(3, result.Items.Count);
        Assert.True(result.Page.HasMore);
        Assert.NotNull(result.Page.NextCursor);
        Assert.Equal(3, result.Page.Size);
    }

    [Fact]
    public async Task Handle_WhenCursorGiven_ReturnsOnlyRowsAfterIt()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        Guid[] newestFirst = await SeedAsync(dbContext, schoolId, 5);

        KeysetResponse<GetSchoolAttendanceSubmissions.Response> first =
            await HandleAsync(dbContext, schoolId, schoolId, pageSize: 2);

        KeysetResponse<GetSchoolAttendanceSubmissions.Response> second = await HandleAsync(
            dbContext, schoolId, schoolId, pageSize: 2, cursor: first.Page.NextCursor);

        Assert.Equal(newestFirst[..2], first.Items.Select(item => item.Id));
        Assert.Equal(newestFirst[2..4], second.Items.Select(item => item.Id));
    }

    /// <summary>
    ///     The whole traversal: five rows, <c>pageSize=2</c>, follow <c>nextCursor</c> until it is
    ///     absent. Every row exactly once, in order, no duplicate and no omission.
    /// </summary>
    [Fact]
    public async Task Handle_WhenFollowingCursorsToTheEnd_ReturnsEveryRowExactlyOnce()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        Guid[] newestFirst = await SeedAsync(dbContext, schoolId, 5);

        Assert.Equal(newestFirst, await TraverseAsync(dbContext, schoolId, pageSize: 2));
    }

    /// <summary>
    ///     The same traversal where <b>every</b> row shares one <c>SubmittedAt</c>, so every page
    ///     boundary is a tie boundary rather than at most one of them.
    /// </summary>
    /// <remarks>
    ///     A fixture of rows with distinct timestamps cannot catch the defect O-06 exists to prevent:
    ///     a cursor that carries only the timestamp skips or repeats <em>at the tie</em> and is exact
    ///     everywhere else. Constructing it this way removes the need to know where the boundary falls
    ///     — with one distinct timestamp across five rows and a page size of two, both boundaries are
    ///     inside the tie by construction.
    ///     <para>
    ///         The guard below asserts the fixture really is degenerate. Without it, a later edit that
    ///         gave the rows distinct instants would leave this test green and testing nothing — which
    ///         is the failure mode this whole file is written against.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task Handle_WhenEverySubmittedAtTies_TraversalStillReturnsEveryRowExactlyOnce()
    {
        Guid schoolId = Guid.NewGuid();
        Guid[] ascendingIds = SubmissionLogSeed.AscendingIds(5);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        await SubmissionLogSeed.SaveAsync(
            dbContext,
            [
                .. ascendingIds.Select(id => SubmissionLogSeed.Log(
                    id, schoolId, SubmissionLogSeed.BaseDate, SubmissionLogSeed.BaseInstant))
            ]);

        // The guard on the fixture: one distinct instant across five rows, and a page size that
        // cannot avoid landing a boundary inside it.
        List<AttendanceSubmissionLog> seeded = dbContext.AttendanceSubmissionLogs
            .Where(log => log.SchoolId == schoolId)
            .ToList();

        Assert.Equal(5, seeded.Count);
        Assert.Single(seeded.Select(log => log.SubmittedAt).Distinct());

        Guid[] visited = await TraverseAsync(dbContext, schoolId, pageSize: 2);

        Assert.Equal(ascendingIds.Reverse(), visited);
        Assert.Equal(5, visited.Distinct().Count());
    }

    /// <summary>
    ///     Plan R-2: <c>nextCursor</c> comes from the <b>last row of the server-ordered page</b>, never
    ///     from an aggregate over it.
    /// </summary>
    /// <remarks>
    ///     <c>items.Max(x =&gt; x.Id)</c> or <c>items.Min(x =&gt; x.Id)</c> would order Guids in C#,
    ///     where <c>Guid.CompareTo</c> disagrees with Postgres' <c>uuid</c> comparison — and it looks
    ///     like tidying. The fixture is built so that the last row's id is <b>neither</b> the maximum
    ///     nor the minimum of the page, which is what makes both mistakes fail here rather than pass
    ///     by coincidence.
    /// </remarks>
    [Fact]
    public async Task Cursor_IsTakenFromTheLastServerOrderedRow()
    {
        Guid schoolId = Guid.NewGuid();
        Guid[] ascendingIds = SubmissionLogSeed.AscendingIds(3);

        // Distinct instants, newest first: largest id, smallest id, middle id. The page of three ends
        // on the middle id, so Max and Min both name a different row.
        Guid newest = ascendingIds[2];
        Guid middle = ascendingIds[0];
        Guid oldest = ascendingIds[1];

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        await SubmissionLogSeed.SaveAsync(
            dbContext,
            SubmissionLogSeed.Log(
                newest, schoolId, SubmissionLogSeed.BaseDate, SubmissionLogSeed.BaseInstant.AddMinutes(3)),
            SubmissionLogSeed.Log(
                middle, schoolId, SubmissionLogSeed.BaseDate, SubmissionLogSeed.BaseInstant.AddMinutes(2)),
            SubmissionLogSeed.Log(
                oldest, schoolId, SubmissionLogSeed.BaseDate, SubmissionLogSeed.BaseInstant.AddMinutes(1)),
            SubmissionLogSeed.Log(
                Guid.NewGuid(), schoolId, SubmissionLogSeed.BaseDate, SubmissionLogSeed.BaseInstant));

        KeysetResponse<GetSchoolAttendanceSubmissions.Response> result =
            await HandleAsync(dbContext, schoolId, schoolId, pageSize: 3);

        Assert.True(SubmissionCursor.TryDecode(result.Page.NextCursor, out SubmissionCursor cursor));

        GetSchoolAttendanceSubmissions.Response last = result.Items[^1];

        Assert.Equal(last.Id, cursor.Id);
        Assert.Equal(last.SubmittedAt, cursor.SubmittedAt);

        // The fixture's own precondition: the assertions above are only meaningful if the last row is
        // not also the extreme one an aggregate would have picked.
        Assert.NotEqual(result.Items.Max(item => item.Id), cursor.Id);
        Assert.NotEqual(result.Items.Min(item => item.Id), cursor.Id);
    }

    /// <summary>
    ///     <c>idempotencyKey</c> is F07's contract and belongs to the client that sent it. Echoing it
    ///     from a read route makes one client's replay token visible to another at the same school.
    /// </summary>
    [Fact]
    public void Handle_DoesNotReturnIdempotencyKey()
    {
        Assert.DoesNotContain(
            Members(typeof(GetSchoolAttendanceSubmissions.Response)),
            name => name.Contains("Idempotency", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Conventions §2: a body must not repeat a route value, and the school is the route.</summary>
    [Fact]
    public void Handle_DoesNotReturnSchoolIdOnListItems()
    {
        Assert.DoesNotContain(
            Members(typeof(GetSchoolAttendanceSubmissions.Response)),
            name => string.Equals(name, "SchoolId", StringComparison.Ordinal));
    }

    /// <summary>
    ///     A guard on the two reflection tests above: they iterate a member set, so an empty one
    ///     satisfies both vacuously.
    /// </summary>
    [Fact]
    public void Response_ExposesTheDocumentedMembers()
    {
        Assert.Equal(
            ["AttendanceDate", "Id", "RecordCount", "SubmittedAt", "SubmittedBy"],
            Members(typeof(GetSchoolAttendanceSubmissions.Response)).Order().ToArray());
    }

    private static string[] Members(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToArray();

    /// <summary>Follows <c>nextCursor</c> to exhaustion and returns every id it saw, in order.</summary>
    private static async Task<Guid[]> TraverseAsync(SparkrockRwcDbContext dbContext, Guid schoolId, int pageSize)
    {
        List<Guid> visited = [];
        string? cursor = null;

        // Bounded, so a cursor that fails to advance is a failing assertion rather than a hung run.
        for (int page = 0; page < 20; page++)
        {
            KeysetResponse<GetSchoolAttendanceSubmissions.Response> result =
                await HandleAsync(dbContext, schoolId, schoolId, pageSize: pageSize, cursor: cursor);

            visited.AddRange(result.Items.Select(item => item.Id));

            if (!result.Page.HasMore)
                return [.. visited];

            cursor = result.Page.NextCursor;
        }

        Assert.Fail("The traversal did not terminate: nextCursor is not advancing.");

        return [];
    }

    /// <summary>
    ///     <paramref name="count" /> logs one minute apart, and the ids in the order the handler should
    ///     return them.
    /// </summary>
    private static async Task<Guid[]> SeedAsync(SparkrockRwcDbContext dbContext, Guid schoolId, int count)
    {
        Guid[] newestFirst = new Guid[count];
        List<AttendanceSubmissionLog> logs = [];

        for (int index = 0; index < count; index++)
        {
            Guid id = Guid.NewGuid();

            newestFirst[count - 1 - index] = id;

            logs.Add(SubmissionLogSeed.Log(
                id, schoolId, SubmissionLogSeed.BaseDate, SubmissionLogSeed.BaseInstant.AddMinutes(index)));
        }

        await SubmissionLogSeed.SaveAsync(dbContext, [.. logs]);

        return newestFirst;
    }

    private static async Task<KeysetResponse<GetSchoolAttendanceSubmissions.Response>> HandleAsync(
        SparkrockRwcDbContext dbContext,
        Guid schoolId,
        Guid authorisedSchoolId,
        Guid? alsoAuthorised = null,
        DateOnly? from = null,
        DateOnly? toExclusive = null,
        int? pageSize = null,
        string? cursor = null)
    {
        Guid[] scope = alsoAuthorised is null ? [authorisedSchoolId] : [authorisedSchoolId, alsoAuthorised.Value];

        return await new GetSchoolAttendanceSubmissions.QueryHandler(
                dbContext, FakeCurrentUser.ScopedTo(scope))
            .Handle(
                new GetSchoolAttendanceSubmissions.Query
                {
                    SchoolId = schoolId,
                    From = from,
                    ToExclusive = toExclusive,
                    PageSize = pageSize,
                    Cursor = cursor
                },
                CancellationToken.None);
    }
}
