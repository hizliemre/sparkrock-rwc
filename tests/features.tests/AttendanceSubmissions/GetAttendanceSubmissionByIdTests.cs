using System.Reflection;
using domain.Attendance;
using domain.Exceptions;
using features.AttendanceSubmissions;
using features.tests.Fakes;
using infra.persistence.postgre;

namespace features.tests.AttendanceSubmissions;

/// <summary>
///     <c>GET /attendance-submissions/{submissionId}</c> — the target of F07's <c>Location</c>.
/// </summary>
/// <remarks>
///     The route exists because F07's <c>201</c> needs somewhere to point (conventions §1), and F01d
///     shipping <c>StudentAttendance.SubmissionId</c> is what lets it answer with more than a header.
///     What it answers with is the rows this submission wrote <b>that have not since been
///     superseded</b> — attendance is a partial upsert (D-08, V-20), so a later submission overwrites
///     the column. <c>supersededCount</c> is that gap made visible on the wire rather than left as a
///     discrepancy the client has to notice.
/// </remarks>
public sealed class GetAttendanceSubmissionByIdHandlerTests
{
    private static readonly Guid Submitter = Guid.Parse("0000000a-0000-0000-0000-00000000000b");

    [Fact]
    public async Task Handle_ReturnsTheHeader()
    {
        Guid schoolId = Guid.NewGuid();
        Guid submissionId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        await SubmissionLogSeed.SaveAsync(
            dbContext,
            SubmissionLogSeed.Log(
                submissionId,
                schoolId,
                SubmissionLogSeed.BaseDate,
                SubmissionLogSeed.BaseInstant,
                recordCount: 0,
                submittedBy: Submitter,
                idempotencyKey: "a-client-key"));

        GetAttendanceSubmissionById.Response response = await HandleAsync(dbContext, submissionId, schoolId);

        Assert.Equal(submissionId, response.Id);
        Assert.Equal(schoolId, response.SchoolId);
        Assert.Equal(SubmissionLogSeed.BaseDate, response.AttendanceDate);
        Assert.Equal(SubmissionLogSeed.BaseInstant, response.SubmittedAt);
        Assert.Equal(0, response.RecordCount);
        Assert.Equal(Submitter, response.SubmittedBy.UserId);
        Assert.Empty(response.Entries);
    }

    /// <summary>
    ///     Unlike the list, this route <b>does</b> return <c>schoolId</c>: it is not school-nested, so
    ///     the school is not otherwise knowable from the request, and the caller has already proven
    ///     authorisation for it.
    /// </summary>
    [Fact]
    public void Response_ReturnsSchoolId()
    {
        Assert.Contains("SchoolId", Members(typeof(GetAttendanceSubmissionById.Response)), StringComparer.Ordinal);
    }

    [Fact]
    public async Task Handle_ReturnsTheAttendanceRowsCarryingTheSubmissionId()
    {
        Guid schoolId = Guid.NewGuid();
        Guid submissionId = Guid.NewGuid();
        Guid otherSubmissionId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        Guid otherStudentId = Guid.NewGuid();
        Guid codeId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        dbContext.Schools.Add(SubmissionLogSeed.School(schoolId));
        dbContext.AttendanceCodes.Add(SubmissionLogSeed.Code(codeId));
        dbContext.Students.Add(SubmissionLogSeed.Student(studentId, schoolId, "Ada", "Byron"));
        dbContext.Students.Add(SubmissionLogSeed.Student(otherStudentId, schoolId, "Grace", "Hopper"));

        await SubmissionLogSeed.SaveAsync(
            dbContext,
            SubmissionLogSeed.Log(
                submissionId, schoolId, SubmissionLogSeed.BaseDate, SubmissionLogSeed.BaseInstant, recordCount: 1),
            SubmissionLogSeed.Log(
                otherSubmissionId,
                schoolId,
                SubmissionLogSeed.BaseDate,
                SubmissionLogSeed.BaseInstant.AddMinutes(1),
                recordCount: 1));

        dbContext.StudentAttendances.Add(SubmissionLogSeed.Attendance(
            Guid.NewGuid(), studentId, schoolId, codeId, SubmissionLogSeed.BaseDate, submissionId, minutesLate: 7));

        // Belongs to a different submission on the same date. Only the submission id selects it.
        dbContext.StudentAttendances.Add(SubmissionLogSeed.Attendance(
            Guid.NewGuid(),
            otherStudentId,
            schoolId,
            codeId,
            SubmissionLogSeed.BaseDate,
            otherSubmissionId));

        await dbContext.SaveChangesAsync(CancellationToken.None);

        GetAttendanceSubmissionById.Response response = await HandleAsync(dbContext, submissionId, schoolId);

        GetAttendanceSubmissionById.Response.EntryInfo entry = Assert.Single(response.Entries);

        Assert.Equal(studentId, entry.StudentId);
        Assert.Equal("Ada", entry.StudentFirstName);
        Assert.Equal("Byron", entry.StudentLastName);
        Assert.Equal("A", entry.AttendCode);
        Assert.Equal("Absent", entry.AttendCodeDescription);
        Assert.True(entry.IsAbsent);
        Assert.False(entry.IsExcused);
        Assert.Equal(7, entry.MinutesLate);
    }

    /// <summary>
    ///     O-01's honest limitation, made visible on the wire (F01d §4.1).
    /// </summary>
    /// <remarks>
    ///     Three rows written, one of them later overwritten by a second submission, which sets that
    ///     row's <c>SubmissionId</c> to the second log. The first submission then returns two entries
    ///     and <c>supersededCount == 1</c>. Non-zero is normal, not an error — and it is preferable to
    ///     silently returning fewer rows than <c>recordCount</c> claims, which is what a header-only
    ///     design would have forced.
    /// </remarks>
    [Fact]
    public async Task Handle_WhenARowWasSupersededByALaterSubmission_ExcludesItAndCountsIt()
    {
        Guid schoolId = Guid.NewGuid();
        Guid firstSubmissionId = Guid.NewGuid();
        Guid secondSubmissionId = Guid.NewGuid();
        Guid codeId = Guid.NewGuid();
        Guid supersededStudentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        dbContext.Schools.Add(SubmissionLogSeed.School(schoolId));
        dbContext.AttendanceCodes.Add(SubmissionLogSeed.Code(codeId));

        Guid[] studentIds = [Guid.NewGuid(), Guid.NewGuid(), supersededStudentId];

        for (int index = 0; index < studentIds.Length; index++)
        {
            dbContext.Students.Add(SubmissionLogSeed.Student(
                studentIds[index], schoolId, "First", $"Last{index}"));
        }

        await SubmissionLogSeed.SaveAsync(
            dbContext,
            SubmissionLogSeed.Log(
                firstSubmissionId,
                schoolId,
                SubmissionLogSeed.BaseDate,
                SubmissionLogSeed.BaseInstant,
                recordCount: 3),
            SubmissionLogSeed.Log(
                secondSubmissionId,
                schoolId,
                SubmissionLogSeed.BaseDate,
                SubmissionLogSeed.BaseInstant.AddMinutes(5),
                recordCount: 1));

        Guid supersededRowId = Guid.NewGuid();

        foreach (Guid studentId in studentIds)
        {
            dbContext.StudentAttendances.Add(SubmissionLogSeed.Attendance(
                studentId == supersededStudentId ? supersededRowId : Guid.NewGuid(),
                studentId,
                schoolId,
                codeId,
                SubmissionLogSeed.BaseDate,
                firstSubmissionId));
        }

        await dbContext.SaveChangesAsync(CancellationToken.None);

        // The partial upsert: the second submission rewrites one existing row, overwriting the column
        // that says which submission wrote it.
        StudentAttendance overwritten = dbContext.StudentAttendances.Single(row => row.Id == supersededRowId);
        overwritten.SubmissionId = secondSubmissionId;
        await dbContext.SaveChangesAsync(CancellationToken.None);

        GetAttendanceSubmissionById.Response response = await HandleAsync(dbContext, firstSubmissionId, schoolId);

        Assert.Equal(3, response.RecordCount);
        Assert.Equal(2, response.Entries.Count);
        Assert.Equal(1, response.SupersededCount);
        Assert.DoesNotContain(response.Entries, entry => entry.StudentId == supersededStudentId);
    }

    /// <summary>
    ///     The invariant itself, asserted directly and over several shapes rather than inferred from
    ///     the one scenario above.
    /// </summary>
    [Theory]
    [InlineData(5, 5, 0)]
    [InlineData(5, 2, 3)]
    [InlineData(1, 0, 1)]
    public async Task Handle_SupersededCountIsRecordCountMinusEntries(
        int recordCount,
        int survivingRows,
        int expectedSuperseded)
    {
        Guid schoolId = Guid.NewGuid();
        Guid submissionId = Guid.NewGuid();
        Guid codeId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        dbContext.Schools.Add(SubmissionLogSeed.School(schoolId));
        dbContext.AttendanceCodes.Add(SubmissionLogSeed.Code(codeId));

        await SubmissionLogSeed.SaveAsync(
            dbContext,
            SubmissionLogSeed.Log(
                submissionId, schoolId, SubmissionLogSeed.BaseDate, SubmissionLogSeed.BaseInstant, recordCount));

        for (int index = 0; index < survivingRows; index++)
        {
            Guid studentId = Guid.NewGuid();

            dbContext.Students.Add(SubmissionLogSeed.Student(studentId, schoolId, "First", $"Last{index}"));
            dbContext.StudentAttendances.Add(SubmissionLogSeed.Attendance(
                Guid.NewGuid(), studentId, schoolId, codeId, SubmissionLogSeed.BaseDate.AddDays(index), submissionId));
        }

        await dbContext.SaveChangesAsync(CancellationToken.None);

        GetAttendanceSubmissionById.Response response = await HandleAsync(dbContext, submissionId, schoolId);

        Assert.Equal(survivingRows, response.Entries.Count);
        Assert.Equal(expectedSuperseded, response.SupersededCount);
        Assert.Equal(response.RecordCount - response.Entries.Count, response.SupersededCount);
    }

    /// <summary>
    ///     A soft-deleted row is hidden by the reflective query filter (DEC-20), so it also reduces
    ///     <c>entries</c> and raises <c>supersededCount</c> — indistinguishable from a supersede here,
    ///     which the spec states rather than hides.
    /// </summary>
    /// <remarks>
    ///     Deleted through <c>Remove()</c> and the interceptor (DEC-21), never by assigning
    ///     <c>IsDeleted</c>: assigning the flag would exercise the query filter while proving nothing
    ///     about the rewrite that production actually performs.
    /// </remarks>
    [Fact]
    public async Task Handle_ExcludesSoftDeletedAttendanceRows()
    {
        Guid schoolId = Guid.NewGuid();
        Guid submissionId = Guid.NewGuid();
        Guid codeId = Guid.NewGuid();
        Guid deletedRowId = Guid.NewGuid();
        Guid deletedStudentId = Guid.NewGuid();
        Guid keptStudentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        dbContext.Schools.Add(SubmissionLogSeed.School(schoolId));
        dbContext.AttendanceCodes.Add(SubmissionLogSeed.Code(codeId));
        dbContext.Students.Add(SubmissionLogSeed.Student(deletedStudentId, schoolId, "Gone", "Away"));
        dbContext.Students.Add(SubmissionLogSeed.Student(keptStudentId, schoolId, "Still", "Here"));

        await SubmissionLogSeed.SaveAsync(
            dbContext,
            SubmissionLogSeed.Log(
                submissionId,
                schoolId,
                SubmissionLogSeed.BaseDate,
                SubmissionLogSeed.BaseInstant,
                recordCount: 2));

        dbContext.StudentAttendances.Add(SubmissionLogSeed.Attendance(
            deletedRowId, deletedStudentId, schoolId, codeId, SubmissionLogSeed.BaseDate, submissionId));
        dbContext.StudentAttendances.Add(SubmissionLogSeed.Attendance(
            Guid.NewGuid(), keptStudentId, schoolId, codeId, SubmissionLogSeed.BaseDate, submissionId));

        await dbContext.SaveChangesAsync(CancellationToken.None);

        dbContext.StudentAttendances.Remove(
            dbContext.StudentAttendances.Single(row => row.Id == deletedRowId));
        await dbContext.SaveChangesAsync(CancellationToken.None);

        GetAttendanceSubmissionById.Response response = await HandleAsync(dbContext, submissionId, schoolId);

        Assert.Equal(keptStudentId, Assert.Single(response.Entries).StudentId);
        Assert.Equal(1, response.SupersededCount);
    }

    /// <summary>
    ///     Ordered <c>lastName, firstName, studentId</c> — the order a roster is read in.
    /// </summary>
    /// <remarks>
    ///     <b>The fixture is built so that each key, removed, produces a different order</b> — which is
    ///     not automatic and was not true of the first version of this test. The surname pair's ids are
    ///     chosen so that ordering them by <c>studentId</c> is the <em>reverse</em> of ordering them by
    ///     <c>firstName</c>, and the <c>Abel</c> student's given name sorts after the <c>Byron</c>
    ///     students' so that dropping <c>lastName</c> reorders the list too. Without those two
    ///     constructions the test passed with either key deleted, because the provider happened to
    ///     return the pairs in the expected order anyway.
    ///     <para>
    ///         The trailing <c>studentId</c> is the one key this tier <b>cannot</b> demonstrate: with
    ///         it removed, two students sharing both names come back in whatever order the provider
    ///         chooses, and "the order it chose" is not evidence that the order is total. That claim is
    ///         asserted on the generated <c>ORDER BY</c> in
    ///         <c>KeysetPagingTests.Detail_EntriesOrderIsTotal</c>.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task Handle_OrdersEntriesByLastNameThenFirstNameThenStudentId()
    {
        Guid schoolId = Guid.NewGuid();
        Guid submissionId = Guid.NewGuid();
        Guid codeId = Guid.NewGuid();

        // Ascending under Comparer<Guid>.Default, which is what EF InMemory sorts by.
        Guid[] ascending = SubmissionLogSeed.AscendingIds(4);

        // Deliberately inverted against the given names: ordering the Byron pair by id puts Zoe first,
        // ordering it by first name puts Ada first. So deleting the firstName key changes the answer.
        Guid byronZoe = ascending[0];
        Guid byronAda = ascending[1];
        Guid[] twins = [ascending[2], ascending[3]];

        // "Zara Abel" rather than "Ada Abel": with the lastName key deleted the list would begin with
        // Ada Byron instead, so that key is demonstrable too.
        Guid abelZara = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        dbContext.Schools.Add(SubmissionLogSeed.School(schoolId));
        dbContext.AttendanceCodes.Add(SubmissionLogSeed.Code(codeId));

        // Added in an order that is wrong on every key, so insertion order cannot pass by accident.
        dbContext.Students.Add(SubmissionLogSeed.Student(twins[1], schoolId, "Zeta", "Zeta"));
        dbContext.Students.Add(SubmissionLogSeed.Student(byronZoe, schoolId, "Zoe", "Byron"));
        dbContext.Students.Add(SubmissionLogSeed.Student(twins[0], schoolId, "Zeta", "Zeta"));
        dbContext.Students.Add(SubmissionLogSeed.Student(byronAda, schoolId, "Ada", "Byron"));
        dbContext.Students.Add(SubmissionLogSeed.Student(abelZara, schoolId, "Zara", "Abel"));

        await SubmissionLogSeed.SaveAsync(
            dbContext,
            SubmissionLogSeed.Log(
                submissionId,
                schoolId,
                SubmissionLogSeed.BaseDate,
                SubmissionLogSeed.BaseInstant,
                recordCount: 5));

        int day = 0;

        foreach (Guid studentId in new[] { twins[1], byronZoe, twins[0], byronAda, abelZara })
        {
            dbContext.StudentAttendances.Add(SubmissionLogSeed.Attendance(
                Guid.NewGuid(),
                studentId,
                schoolId,
                codeId,
                SubmissionLogSeed.BaseDate.AddDays(day++),
                submissionId));
        }

        await dbContext.SaveChangesAsync(CancellationToken.None);

        GetAttendanceSubmissionById.Response response = await HandleAsync(dbContext, submissionId, schoolId);

        Assert.Equal(
            [abelZara, byronAda, byronZoe, twins[0], twins[1]],
            response.Entries.Select(entry => entry.StudentId));
    }

    [Fact]
    public async Task Handle_WhenSubmissionUnknown_ThrowsNotFound()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => HandleAsync(dbContext, Guid.NewGuid(), Guid.NewGuid()));

        Assert.Equal(ErrorCodes.Attendance.SubmissionNotFound, exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_WhenSubmissionOutsideScope_ThrowsNotFound()
    {
        Guid otherSchoolId = Guid.NewGuid();
        Guid submissionId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        await SubmissionLogSeed.SaveAsync(
            dbContext,
            SubmissionLogSeed.Log(
                submissionId, otherSchoolId, SubmissionLogSeed.BaseDate, SubmissionLogSeed.BaseInstant));

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => HandleAsync(dbContext, submissionId, Guid.NewGuid()));

        Assert.Equal(ErrorCodes.Attendance.SubmissionNotFound, exception.ErrorCode);
    }

    /// <summary>
    ///     The existence oracle: a submission that does not exist and one belonging to another school
    ///     are indistinguishable.
    /// </summary>
    /// <remarks>
    ///     Structural rather than disciplined — the scope predicate is part of the lookup, so both
    ///     cases take the same single <c>null</c> branch, and <see cref="NotFoundException" /> carries
    ///     no message to differ in.
    /// </remarks>
    [Fact]
    public async Task Handle_UnknownAndOutOfScopeProduceIdenticalExceptions()
    {
        Guid otherSchoolId = Guid.NewGuid();
        Guid callersSchoolId = Guid.NewGuid();
        Guid realButUnauthorised = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        await SubmissionLogSeed.SaveAsync(
            dbContext,
            SubmissionLogSeed.Log(
                realButUnauthorised, otherSchoolId, SubmissionLogSeed.BaseDate, SubmissionLogSeed.BaseInstant));

        NotFoundException unknown = await Assert.ThrowsAsync<NotFoundException>(
            () => HandleAsync(dbContext, Guid.NewGuid(), callersSchoolId));

        NotFoundException unauthorised = await Assert.ThrowsAsync<NotFoundException>(
            () => HandleAsync(dbContext, realButUnauthorised, callersSchoolId));

        Assert.Equal(unknown.ErrorCode, unauthorised.ErrorCode);
        Assert.Equal(unknown.Message, unauthorised.Message);
        Assert.Equal(unknown.GetType(), unauthorised.GetType());
    }

    /// <summary>
    ///     O-17 is F06's and F08's to decide, and F11 does not pre-empt it. The submission log answers
    ///     "what was submitted"; <c>Notes</c> is the one field that carries safeguarding and health
    ///     detail, and this route has no need of it.
    /// </summary>
    [Fact]
    public void Response_DoesNotReturnNotes()
    {
        Assert.DoesNotContain(
            Members(typeof(GetAttendanceSubmissionById.Response.EntryInfo)),
            name => name.Contains("Notes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Response_DoesNotReturnIdempotencyKey()
    {
        Assert.DoesNotContain(
            Members(typeof(GetAttendanceSubmissionById.Response))
                .Concat(Members(typeof(GetAttendanceSubmissionById.Response.EntryInfo))),
            name => name.Contains("Idempotency", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     A guard on the three reflection tests: each iterates a member set, so an empty one satisfies
    ///     all of them vacuously.
    /// </summary>
    [Fact]
    public void Response_ExposesTheDocumentedMembers()
    {
        Assert.Equal(
            [
                "AttendanceDate", "Entries", "Id", "RecordCount", "SchoolId", "SubmittedAt",
                "SubmittedBy", "SupersededCount"
            ],
            Members(typeof(GetAttendanceSubmissionById.Response)).Order().ToArray());

        Assert.Equal(
            [
                "AttendCode", "AttendCodeDescription", "IsAbsent", "IsExcused", "MinutesLate",
                "StudentFirstName", "StudentId", "StudentLastName"
            ],
            Members(typeof(GetAttendanceSubmissionById.Response.EntryInfo)).Order().ToArray());
    }

    private static string[] Members(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToArray();

    private static async Task<GetAttendanceSubmissionById.Response> HandleAsync(
        SparkrockRwcDbContext dbContext,
        Guid submissionId,
        Guid authorisedSchoolId) =>
        await new GetAttendanceSubmissionById.QueryHandler(dbContext, FakeCurrentUser.ScopedTo(authorisedSchoolId))
            .Handle(new GetAttendanceSubmissionById.Query { SubmissionId = submissionId }, CancellationToken.None);
}
