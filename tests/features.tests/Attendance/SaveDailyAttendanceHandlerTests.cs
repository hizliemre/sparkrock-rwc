using domain.Attendance;
using domain.Exceptions;
using domain.Students;
using features.Attendance;
using features.tests.Fakes;
using Microsoft.EntityFrameworkCore;

namespace features.tests.Attendance;

/// <summary>
///     Stages B and C of spec §2, the prior-count arithmetic of §1, and the attendance upsert.
/// </summary>
/// <remarks>
///     Handler tier throughout: every assertion here is about LINQ semantics, the change tracker, the
///     soft-delete filter or the order in which checks run. Nothing here touches concurrency — VC-35
///     makes every such assertion vacuous on EF InMemory, and they all live in
///     <c>features.integration.tests</c>.
/// </remarks>
public sealed class SaveDailyAttendanceHandlerTests
{
    // ------------------------------------------------------------------ B. the addressed resource

    /// <summary>
    ///     A non-admin identity is essential: the production stub is <c>IsSystemAdmin = true</c>, so a
    ///     test that forgot this would exercise nothing.
    /// </summary>
    [Fact]
    public async Task Handle_WhenSchoolIsOutsideAuthorizedSchoolIds_ThrowsNotFound()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            fixture.Handler(currentUser: FakeCurrentUser.ScopedTo(Guid.NewGuid()))
                .Handle(fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None));

        Assert.Equal(ErrorCodes.School.NotFound, exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_WhenSchoolDoesNotExist_ThrowsNotFound()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();

        Guid absent = Guid.NewGuid();

        SaveDailyAttendance.Command command = new()
        {
            SchoolId = absent,
            Date = "2026-09-14",
            Entries = [AttendanceSubmissionFixture.Entry(Guid.NewGuid())]
        };

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            fixture.Handler(currentUser: FakeCurrentUser.ScopedTo(absent)).Handle(command, CancellationToken.None));

        Assert.Equal(ErrorCodes.School.NotFound, exception.ErrorCode);
    }

    /// <summary>
    ///     Conventions §2: a cross-tenant 404 and a genuine not-found 404 must emit an identical
    ///     payload, or the status is an existence oracle. <see cref="NotFoundException" /> takes no
    ///     message parameter, so this holds by construction.
    /// </summary>
    [Fact]
    public async Task Handle_WhenSchoolIsAbsentAndWhenSchoolIsOutOfScope_ProducesIdenticalExceptions()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        Guid absent = Guid.NewGuid();

        NotFoundException fromAbsent = await Assert.ThrowsAsync<NotFoundException>(() =>
            fixture.Handler(currentUser: FakeCurrentUser.ScopedTo(absent)).Handle(
                new SaveDailyAttendance.Command
                {
                    SchoolId = absent,
                    Date = "2026-09-14",
                    Entries = [AttendanceSubmissionFixture.Entry(student.Id)]
                },
                CancellationToken.None));

        NotFoundException fromOutOfScope = await Assert.ThrowsAsync<NotFoundException>(() =>
            fixture.Handler(currentUser: FakeCurrentUser.ScopedTo(Guid.NewGuid())).Handle(
                fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)),
                CancellationToken.None));

        Assert.Equal(fromAbsent.ErrorCode, fromOutOfScope.ErrorCode);
        Assert.Equal(fromAbsent.Message, fromOutOfScope.Message);
    }

    /// <summary><b>V-14 ●, the school half.</b> 409, because the school is the addressed resource.</summary>
    [Fact]
    public async Task Handle_WhenSchoolIsInactive_ThrowsConflict()
    {
        await using AttendanceSubmissionFixture fixture =
            await AttendanceSubmissionFixture.CreateAsync(schoolIsActive: false);

        Student student = await fixture.AddStudentAsync();

        ConflictException exception = await Assert.ThrowsAsync<ConflictException>(() =>
            fixture.Handler().Handle(
                fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None));

        Assert.Equal(ErrorCodes.School.Inactive, exception.ErrorCode);
    }

    /// <summary>
    ///     The ordering assertion. One exception cannot be both a 409 and a 400, so the addressed
    ///     resource has to be settled before anything about the body accumulates.
    /// </summary>
    [Fact]
    public async Task Handle_WhenSchoolIsInactiveAndAnEntryIsInvalid_ThrowsConflictNotBusinessRule()
    {
        await using AttendanceSubmissionFixture fixture =
            await AttendanceSubmissionFixture.CreateAsync(schoolIsActive: false);

        ConflictException exception = await Assert.ThrowsAsync<ConflictException>(() =>
            fixture.Handler().Handle(
                fixture.Command(AttendanceSubmissionFixture.Entry(
                    Guid.NewGuid(), AttendanceSubmissionFixture.UnknownCode)),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.School.Inactive, exception.ErrorCode);
    }

    /// <summary>
    ///     <b>V-25 ●'s <c>Verified by</c>, and the test that fails if anyone uses <c>UtcNow.Date</c>.</b>
    /// </summary>
    /// <remarks>
    ///     At <c>2026-09-14T02:00Z</c> the school's local date is still the 13th (America/Toronto is
    ///     UTC−4 in September), so submitting the 14th is submitting the future. A handler resolving
    ///     "today" in UTC would accept it — which is DEC-12's whole point: <c>UtcNow.Date</c> rolls the
    ///     attendance date at midnight UTC, mid-evening for this school.
    /// </remarks>
    [Fact]
    public async Task Handle_WhenDateIsAfterSchoolLocalToday_ThrowsBusinessRuleException()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync(
            utcNow: new DateTimeOffset(2026, 9, 14, 2, 0, 0, TimeSpan.Zero));

        Student student = await fixture.AddStudentAsync();

        BusinessRuleException exception = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            fixture.Handler().Handle(
                fixture.CommandOn(new DateOnly(2026, 9, 14), AttendanceSubmissionFixture.Entry(student.Id)),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.Attendance.DateOutOfRange, exception.ErrorCode);

        Violation violation = Assert.Single(exception.Violations);
        Assert.Equal("path", violation.Source);
        Assert.Equal(nameof(SaveDailyAttendance.Command.Date), violation.Path);
        Assert.Equal(ErrorCodes.Attendance.DateOutOfRange, violation.Code);
    }

    /// <summary>The same clock, one day earlier — school-local today is accepted.</summary>
    [Fact]
    public async Task Handle_WhenDateIsSchoolLocalToday_Succeeds()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync(
            utcNow: new DateTimeOffset(2026, 9, 14, 2, 0, 0, TimeSpan.Zero));

        Student student = await fixture.AddStudentAsync();

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.CommandOn(new DateOnly(2026, 9, 13), AttendanceSubmissionFixture.Entry(student.Id)),
            CancellationToken.None);

        Assert.Equal(new DateOnly(2026, 9, 13), response.AttendanceDate);
    }

    [Fact]
    public async Task Handle_WhenDateIsOlderThanTheBackDatingWindow_ThrowsBusinessRuleException()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        BusinessRuleException exception = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            fixture.Handler(backDatingWindowDays: 30).Handle(
                fixture.CommandOn(
                    AttendanceSubmissionFixture.SubmittedDate.AddDays(-31),
                    AttendanceSubmissionFixture.Entry(student.Id)),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.Attendance.DateOutOfRange, exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_WhenDateIsWithinTheBackDatingWindow_Succeeds()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        SaveDailyAttendance.Response response = await fixture.Handler(backDatingWindowDays: 30).Handle(
            fixture.CommandOn(
                AttendanceSubmissionFixture.SubmittedDate.AddDays(-30),
                AttendanceSubmissionFixture.Entry(student.Id)),
            CancellationToken.None);

        Assert.Equal(AttendanceSubmissionFixture.SubmittedDate.AddDays(-30), response.AttendanceDate);
    }

    /// <summary>D-03, preserved: attendance outside any term is recorded with no term, not refused.</summary>
    [Fact]
    public async Task Handle_WhenNoActiveTermCoversTheDate_LeavesTermIdNull()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None);

        Assert.Null(response.TermId);
        Assert.Null((await Rows(fixture)).Single().TermId);
    }

    /// <summary>
    ///     Bounds are <b>closed</b> — <c>[StartDate, EndDate]</c> — the one deliberate exception to the
    ///     half-open rule, because D-03 preserves legacy's <c>BETWEEN</c>. Both endpoints are asserted
    ///     because design §3 says F07 must not read <c>EndDate</c> as exclusive.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-13)]
    public async Task Handle_WhenAnActiveTermCoversTheDate_ResolvesIt(int offsetFromEnd)
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        DateOnly attendDate = AttendanceSubmissionFixture.SubmittedDate.AddDays(offsetFromEnd);

        domain.SchoolTerms.SchoolTerm term = await fixture.AddTermAsync(
            AttendanceSubmissionFixture.SubmittedDate.AddDays(-13),
            AttendanceSubmissionFixture.SubmittedDate);

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.CommandOn(attendDate, AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None);

        Assert.Equal(term.Id, response.TermId);
        Assert.Equal(term.Id, (await Rows(fixture)).Single().TermId);
    }

    [Fact]
    public async Task Handle_WhenOnlyAnInactiveTermCoversTheDate_LeavesTermIdNull()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        await fixture.AddTermAsync(
            AttendanceSubmissionFixture.SubmittedDate.AddDays(-13),
            AttendanceSubmissionFixture.SubmittedDate,
            isActive: false);

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None);

        Assert.Null(response.TermId);
    }

    /// <summary>
    ///     The bound the validator cannot own, because <c>ViolationSource</c> never infers
    ///     <c>header</c>.
    /// </summary>
    [Fact]
    public async Task Handle_WhenIdempotencyKeyExceedsSixtyFourCharacters_ThrowsBusinessRuleExceptionWithHeaderSource()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        SaveDailyAttendance.Command command = new()
        {
            SchoolId = fixture.SchoolId,
            Date = "2026-09-14",
            IdempotencyKey = new string('k', AttendanceSave.MaxIdempotencyKeyLength + 1),
            Entries = [AttendanceSubmissionFixture.Entry(student.Id)]
        };

        BusinessRuleException exception = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            fixture.Handler().Handle(command, CancellationToken.None));

        Violation violation = Assert.Single(exception.Violations);
        Assert.Equal("header", violation.Source);
        Assert.Equal(SaveDailyAttendance.IdempotencyKeyHeader, violation.Path);
        Assert.Equal(ErrorCodes.Validation.Failed, violation.Code);
    }

    // -------------------------------------------------------------- C. the body, and the oracle

    /// <summary>
    ///     <b>Conventions §2 names this test.</b> They are <c>record</c>s, so structural equality covers
    ///     <c>Source</c>, <c>Path</c>, <c>Code</c> and <c>Message</c> in one assertion — the only
    ///     difference permitted is the index, and the two are built at the same index here.
    /// </summary>
    [Fact]
    public async Task Handle_WhenStudentIsUnknownAndWhenStudentIsFromAnotherSchool_ProducesByteIdenticalViolations()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();

        Student elsewhere = await fixture.AddStudentAsync(schoolId: Guid.NewGuid());

        Violation fromUnknown = await SingleViolation(fixture, AttendanceSubmissionFixture.Entry(Guid.NewGuid()));
        Violation fromOtherSchool = await SingleViolation(fixture, AttendanceSubmissionFixture.Entry(elsewhere.Id));

        Assert.Equal(fromUnknown, fromOtherSchool);
        Assert.Equal(ErrorCodes.Attendance.StudentNotOnRoster, fromUnknown.Code);
    }

    /// <summary>
    ///     <b>V-13 ●'s <c>Verified by</c>.</b> DEC-08's documented limitation: <c>Student.SchoolId</c> is
    ///     a single mutable FK with no enrolment history, so after a transfer a back-dated correction
    ///     for the former school is refused — and the message is deliberately the same one an unknown id
    ///     gets, so the endpoint cannot explain why.
    /// </summary>
    [Fact]
    public async Task Handle_WhenStudentHasTransferredAway_ReportsNotOnRoster()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();

        Student student = await fixture.AddStudentAsync();
        student.SchoolId = Guid.NewGuid();
        await fixture.DbContext.SaveChangesAsync(CancellationToken.None);

        Violation violation = await SingleViolation(fixture, AttendanceSubmissionFixture.Entry(student.Id));

        Assert.Equal(ErrorCodes.Attendance.StudentNotOnRoster, violation.Code);
    }

    [Fact]
    public async Task Handle_WhenStudentIsNotOnTheRoster_UsesTheNotOnRosterCodeAndMessageWithoutTheId()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();

        Guid unknown = Guid.NewGuid();

        Violation violation = await SingleViolation(fixture, AttendanceSubmissionFixture.Entry(unknown));

        Assert.Equal(SaveDailyAttendance.StudentNotOnRosterMessage, violation.Message);
        Assert.DoesNotContain(unknown.ToString(), violation.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("body", violation.Source);
        Assert.Equal("Entries[0].StudentId", violation.Path);
    }

    /// <summary>
    ///     <b>V-04 ●'s <c>Verified by</c>.</b> Legacy stored an unrecognised code as present-unexcused
    ///     (L-06) and the row was then invisible to every read; the submission is now rejected.
    /// </summary>
    [Fact]
    public async Task Handle_WhenAttendCodeIsUnknown_ThrowsBusinessRuleExceptionWithUnknownCode()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        Violation violation = await SingleViolation(
            fixture, AttendanceSubmissionFixture.Entry(student.Id, AttendanceSubmissionFixture.UnknownCode));

        Assert.Equal(ErrorCodes.Attendance.UnknownCode, violation.Code);
        Assert.Equal("Entries[0].AttendCode", violation.Path);
    }

    /// <summary>
    ///     <b>V-14 ●, the code half</b> — as a <b>400</b> field error, which conventions §2 rules and
    ///     which supersedes V-14's original 409 for codes.
    /// </summary>
    /// <remarks>
    ///     The two fixtures submit the <em>same</em> code string; one has it seeded as inactive and the
    ///     other has never heard of it. Identical violations is then a real assertion rather than an
    ///     artefact of two different code values producing two different messages.
    /// </remarks>
    [Fact]
    public async Task Handle_WhenAttendCodeIsInactive_ProducesTheSameViolationAsUnknown()
    {
        await using AttendanceSubmissionFixture withInactive = await AttendanceSubmissionFixture.CreateAsync();
        Student first = await withInactive.AddStudentAsync();

        Violation fromInactive = await SingleViolation(
            withInactive, AttendanceSubmissionFixture.Entry(first.Id, AttendanceSubmissionFixture.InactiveCode));

        // A second database where that same value was never created at all. Deleting it from the
        // first is not an option — DEC-20 makes an AttendanceCode undeletable and the interceptor
        // throws — and using a different string would make the comparison meaningless, because the
        // message echoes the code.
        await using AttendanceSubmissionFixture withoutIt =
            await AttendanceSubmissionFixture.CreateAsync(seedInactiveCode: false);

        Student second = await withoutIt.AddStudentAsync();

        Violation fromUnknown = await SingleViolation(
            withoutIt, AttendanceSubmissionFixture.Entry(second.Id, AttendanceSubmissionFixture.InactiveCode));

        Assert.Equal(fromInactive, fromUnknown);
    }

    /// <summary>
    ///     <b>V-27.</b> SQL Server folded case, so <c>a</c> and <c>A</c> were one code; Postgres unique
    ///     indexes do not, and F01c added a check constraint forcing uppercase. Normalising before the
    ///     ordinal lookup preserves the legacy meaning — without it <c>"a"</c> is an unknown code.
    /// </summary>
    [Fact]
    public async Task Handle_WhenAttendCodeIsLowerCase_ResolvesTheUpperCaseCode()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(student.Id, "a")), CancellationToken.None);

        // The normalised value is what is snapshotted and echoed, not the value that was sent.
        Assert.Equal(AttendanceSubmissionFixture.AbsentCode, response.Entries[0].AttendCode);
        Assert.Equal(AttendanceSubmissionFixture.AbsentCode, (await Rows(fixture)).Single().AttendCode);
    }

    /// <summary>
    ///     The assertion that fails if anyone stages the reference checks. Design §4: a form with a bad
    ///     student <em>and</em> a bad code otherwise takes three round trips to fix.
    /// </summary>
    [Fact]
    public async Task Handle_WhenBothAStudentAndACodeAreInvalid_ReportsBothInOneException()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        BusinessRuleException exception = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            fixture.Handler().Handle(
                fixture.Command(
                    AttendanceSubmissionFixture.Entry(Guid.NewGuid()),
                    AttendanceSubmissionFixture.Entry(student.Id, AttendanceSubmissionFixture.UnknownCode)),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.Attendance.SubmissionRejected, exception.ErrorCode);
        Assert.Equal(2, exception.Violations.Count);
        Assert.Equal(ErrorCodes.Attendance.StudentNotOnRoster, exception.Violations[0].Code);
        Assert.Equal(ErrorCodes.Attendance.UnknownCode, exception.Violations[1].Code);
    }

    /// <summary>
    ///     Sorted by entry index, then by field name, so the byte-identity assertions above are stable
    ///     rather than accidentally so.
    /// </summary>
    [Fact]
    public async Task Handle_WhenMultipleEntriesAreInvalid_OrdersViolationsByEntryIndex()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        BusinessRuleException exception = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            fixture.Handler().Handle(
                fixture.Command(
                    AttendanceSubmissionFixture.Entry(student.Id),
                    AttendanceSubmissionFixture.Entry(Guid.NewGuid(), AttendanceSubmissionFixture.UnknownCode),
                    AttendanceSubmissionFixture.Entry(Guid.NewGuid())),
                CancellationToken.None));

        Assert.Equal(
            ["Entries[1].AttendCode", "Entries[1].StudentId", "Entries[2].StudentId"],
            exception.Violations.Select(violation => violation.Path));
    }

    [Fact]
    public async Task Handle_WhenTheBodyIsInvalid_WritesNothing()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            fixture.Handler().Handle(
                fixture.Command(AttendanceSubmissionFixture.Entry(Guid.NewGuid())), CancellationToken.None));

        await using infra.persistence.postgre.SparkrockRwcDbContext reader = fixture.NewContext();

        Assert.Empty(await reader.StudentAttendances.ToListAsync());
        Assert.Empty(await reader.StudentAttendanceSummaries.ToListAsync());
        Assert.Empty(await reader.StudentAlerts.ToListAsync());
        Assert.Empty(await reader.AttendanceSubmissionLogs.ToListAsync());
    }

    /// <summary>
    ///     <b>Spec §8, and it must fail if an <c>IsActive</c> predicate is ever added to the student
    ///     query.</b> legacy-analysis §4 lists "attendance accepted for inactive students" as a
    ///     preserved behaviour — a student deactivated mid-year still needs corrections and back-fill.
    /// </summary>
    [Fact]
    public async Task Handle_WhenStudentIsInactive_RecordsAttendance()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync(isActive: false);

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None);

        Assert.Equal(SaveDailyAttendance.OutcomeCreated, response.Entries[0].Outcome);
        Assert.Single(await Rows(fixture));
    }

    // ------------------------------------------------------------------- §1, the ten-case table

    /// <summary>Case 1: no prior row anywhere, submitted absent.</summary>
    [Fact]
    public async Task Handle_WhenStudentHasNoPriorAbsenceAndIsMarkedAbsent_TotalIsOne()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None);

        Assert.Equal(1, response.Entries[0].TotalAbsences);
    }

    /// <summary>Case 2: no prior row anywhere, submitted present — the summary is still created, at zero.</summary>
    [Fact]
    public async Task Handle_WhenStudentHasNoPriorAbsenceAndIsMarkedPresent_TotalIsZero()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(
                AttendanceSubmissionFixture.Entry(student.Id, AttendanceSubmissionFixture.PresentCode)),
            CancellationToken.None);

        Assert.Equal(0, response.Entries[0].TotalAbsences);
    }

    /// <summary>
    ///     <b>Case 3, and the test that fails if <c>AttendDate &lt;&gt; date</c> is dropped from the
    ///     recount.</b>
    /// </summary>
    /// <remarks>
    ///     Five prior absences of which one is today; resubmitting today as present must give four.
    ///     Without the exclusion today's committed row is counted by the query and the total never
    ///     falls — a corrected absence stays in the count for the rest of the year, and an open
    ///     safeguarding alert never auto-resolves.
    /// </remarks>
    [Fact]
    public async Task Handle_WhenTodaysAbsenceIsCorrectedToPresent_TotalDecreases()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        for (int day = 1; day <= 4; day++)
            await fixture.AddAttendanceAsync(student.Id, AttendanceSubmissionFixture.SubmittedDate.AddDays(-day));

        await fixture.AddAttendanceAsync(student.Id, AttendanceSubmissionFixture.SubmittedDate);

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(
                AttendanceSubmissionFixture.Entry(student.Id, AttendanceSubmissionFixture.PresentCode)),
            CancellationToken.None);

        Assert.Equal(4, response.Entries[0].TotalAbsences);
    }

    /// <summary>Case 4: today's present corrected to absent.</summary>
    [Fact]
    public async Task Handle_WhenTodaysPresentIsCorrectedToAbsent_TotalIncreases()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        await fixture.AddAttendanceAsync(student.Id, AttendanceSubmissionFixture.SubmittedDate.AddDays(-1));
        await fixture.AddAttendanceAsync(student.Id, AttendanceSubmissionFixture.SubmittedDate, isAbsent: false);

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None);

        Assert.Equal(2, response.Entries[0].TotalAbsences);
    }

    /// <summary>Case 5: idempotent in value, and the row is still rewritten.</summary>
    [Fact]
    public async Task Handle_WhenTodaysAbsenceIsResubmittedAsAbsent_TotalIsUnchanged()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        await fixture.AddAttendanceAsync(student.Id, AttendanceSubmissionFixture.SubmittedDate.AddDays(-1));
        StudentAttendance today =
            await fixture.AddAttendanceAsync(student.Id, AttendanceSubmissionFixture.SubmittedDate);

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None);

        Assert.Equal(2, response.Entries[0].TotalAbsences);
        Assert.Equal(SaveDailyAttendance.OutcomeUpdated, response.Entries[0].Outcome);
        Assert.Equal(today.Id, response.Entries[0].AttendanceId);
        Assert.Equal(response.SubmissionId, (await Rows(fixture)).Single(row => row.Id == today.Id).SubmissionId);
    }

    /// <summary>Case 6: one grouped query, several prior absences.</summary>
    [Fact]
    public async Task Handle_WhenStudentHasSeveralPriorAbsences_CountsThemAll()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        for (int day = 1; day <= 6; day++)
            await fixture.AddAttendanceAsync(student.Id, AttendanceSubmissionFixture.SubmittedDate.AddDays(-day));

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None);

        Assert.Equal(7, response.Entries[0].TotalAbsences);
    }

    /// <summary>Case 7: soft-deleted prior rows are excluded by the reflective filter.</summary>
    [Fact]
    public async Task Handle_WhenPriorAbsenceIsSoftDeleted_ExcludesItFromTheTotal()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        await fixture.AddAttendanceAsync(student.Id, AttendanceSubmissionFixture.SubmittedDate.AddDays(-1));

        StudentAttendance withdrawn = await fixture.AddAttendanceAsync(
            student.Id, AttendanceSubmissionFixture.SubmittedDate.AddDays(-2));

        // Remove() + SaveChangesAsync, never `IsDeleted =` — DEC-21 makes the interceptor the only
        // writer of that column, and it is the interceptor's rewrite that is under test here.
        fixture.DbContext.StudentAttendances.Remove(withdrawn);
        await fixture.DbContext.SaveChangesAsync(CancellationToken.None);

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None);

        Assert.Equal(2, response.Entries[0].TotalAbsences);
    }

    /// <summary><b>Case 8, V-07b's <c>Verified by</c>.</b> The legacy predicate filtered nothing (L-12).</summary>
    [Fact]
    public async Task Handle_WhenStudentHasAbsencesInThePriorSchoolYear_ExcludesThemFromTheTotal()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        await fixture.AddAttendanceAsync(student.Id, new DateOnly(2026, 6, 10));
        await fixture.AddAttendanceAsync(student.Id, new DateOnly(2026, 8, 31));

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None);

        Assert.Equal(1, response.Entries[0].TotalAbsences);
    }

    /// <summary>
    ///     <b>Case 9, V-07c ●'s <c>Verified by</c>. Fails the moment a <c>SchoolId</c> term or a
    ///     <c>WhereAuthorized</c> is added to the recount.</b>
    /// </summary>
    [Fact]
    public async Task Handle_WhenStudentHasAbsencesAtAnotherSchoolThisYear_IncludesThemInTheTotal()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        Guid formerSchool = Guid.NewGuid();

        await fixture.AddAttendanceAsync(
            student.Id, AttendanceSubmissionFixture.SubmittedDate.AddDays(-10), schoolId: formerSchool);
        await fixture.AddAttendanceAsync(
            student.Id, AttendanceSubmissionFixture.SubmittedDate.AddDays(-11), schoolId: formerSchool);

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None);

        Assert.Equal(3, response.Entries[0].TotalAbsences);
    }

    /// <summary>The <c>TryGetValue</c>-versus-indexer trap: a clean record is absent from the dictionary.</summary>
    [Fact]
    public async Task Handle_WhenAStudentHasNoPriorRows_DoesNotThrowOnTheLookup()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();

        Student withHistory = await fixture.AddStudentAsync();
        Student clean = await fixture.AddStudentAsync();

        await fixture.AddAttendanceAsync(withHistory.Id, AttendanceSubmissionFixture.SubmittedDate.AddDays(-1));

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(
                AttendanceSubmissionFixture.Entry(withHistory.Id),
                AttendanceSubmissionFixture.Entry(clean.Id)),
            CancellationToken.None);

        Assert.Equal(2, response.Entries.Single(entry => entry.StudentId == withHistory.Id).TotalAbsences);
        Assert.Equal(1, response.Entries.Single(entry => entry.StudentId == clean.Id).TotalAbsences);
    }

    /// <summary>
    ///     <b>V-20's <c>Verified by</c>.</b> A submission is a partial upsert over the students it lists
    ///     (D-08). An omitted student is untouched — not defaulted to present, not deleted, and their
    ///     summary is not recomputed.
    /// </summary>
    [Fact]
    public async Task Handle_WhenAStudentIsOmittedFromThePayload_LeavesTheirAttendanceAndSummaryUntouched()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();

        Student submitted = await fixture.AddStudentAsync();
        Student omitted = await fixture.AddStudentAsync();

        StudentAttendance omittedRow = await fixture.AddAttendanceAsync(
            omitted.Id, AttendanceSubmissionFixture.SubmittedDate);
        StudentAttendanceSummary omittedSummary = await fixture.AddSummaryAsync(omitted.Id, totalAbsences: 7);

        await fixture.Handler().Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(submitted.Id)), CancellationToken.None);

        await using infra.persistence.postgre.SparkrockRwcDbContext reader = fixture.NewContext();

        StudentAttendance untouched = await reader.StudentAttendances.SingleAsync(row => row.Id == omittedRow.Id);
        Assert.Null(untouched.ModifiedAt);
        Assert.Null(untouched.SubmissionId);
        Assert.True(untouched.IsAbsent);

        StudentAttendanceSummary summary =
            await reader.StudentAttendanceSummaries.SingleAsync(row => row.Id == omittedSummary.Id);
        Assert.Equal(7, summary.TotalAbsences);
        Assert.Null(summary.ModifiedAt);
    }

    // ------------------------------------------------------- T07-09, the upsert and the snapshot

    /// <summary>
    ///     <b>V-01's <c>Verified by</c> — the exact shape of legacy's stale <c>@ExistingID</c>.</b>
    /// </summary>
    /// <remarks>
    ///     <c>SELECT @var = …</c> matching no rows leaves the variable unchanged, and <c>@ExistingID</c>
    ///     was never reset between cursor iterations. So once <em>any</em> student in the batch had a
    ///     row, every later student without one took the UPDATE branch and re-wrote <em>that</em>
    ///     student's row: one record overwritten repeatedly, and the others never inserted. Three
    ///     students, only the second with a pre-existing row, is the minimal reproduction.
    /// </remarks>
    [Fact]
    public async Task Handle_WhenOneStudentHasAnExistingRowAndAnotherDoesNot_UpdatesOnlyTheFirstAndInsertsTheSecond()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();

        Student first = await fixture.AddStudentAsync();
        Student second = await fixture.AddStudentAsync();
        Student third = await fixture.AddStudentAsync();

        StudentAttendance secondsRow = await fixture.AddAttendanceAsync(
            second.Id, AttendanceSubmissionFixture.SubmittedDate, notes: "seeded");

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(
                AttendanceSubmissionFixture.Entry(first.Id, AttendanceSubmissionFixture.AbsentCode),
                AttendanceSubmissionFixture.Entry(second.Id, AttendanceSubmissionFixture.ExcusedCode),
                AttendanceSubmissionFixture.Entry(third.Id, AttendanceSubmissionFixture.PresentCode)),
            CancellationToken.None);

        IReadOnlyList<StudentAttendance> rows = await Rows(fixture);

        Assert.Equal(3, rows.Count);
        Assert.Equal(3, rows.Select(row => row.StudentId).Distinct().Count());

        // Exactly one update, and it is the row that already existed.
        Assert.Equal(2, response.CreatedCount);
        Assert.Equal(1, response.UpdatedCount);
        Assert.Equal(secondsRow.Id, rows.Single(row => row.StudentId == second.Id).Id);

        // The first student's row carries the first student's code, not the third's.
        Assert.Equal(AttendanceSubmissionFixture.AbsentCode, rows.Single(row => row.StudentId == first.Id).AttendCode);
    }

    /// <summary>
    ///     <b>V-02's <c>Verified by</c>.</b> Legacy's <c>@IsAbsent</c>/<c>@IsExcused</c> went stale on an
    ///     unrecognised code, and because <c>AttendanceCodes.IsAbsent</c> is <c>NOT NULL</c>, once any
    ///     known code had been read the guard could never fire again — so every later unrecognised code
    ///     silently inherited the previous student's flags.
    /// </summary>
    [Fact]
    public async Task Handle_WhenEntriesHaveDifferentCodes_SnapshotsEachEntrysOwnFlags()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();

        Student absent = await fixture.AddStudentAsync();
        Student excused = await fixture.AddStudentAsync();
        Student present = await fixture.AddStudentAsync();

        await fixture.Handler().Handle(
            fixture.Command(
                AttendanceSubmissionFixture.Entry(absent.Id, AttendanceSubmissionFixture.AbsentCode),
                AttendanceSubmissionFixture.Entry(excused.Id, AttendanceSubmissionFixture.ExcusedCode),
                AttendanceSubmissionFixture.Entry(present.Id, AttendanceSubmissionFixture.PresentCode)),
            CancellationToken.None);

        IReadOnlyList<StudentAttendance> rows = await Rows(fixture);

        StudentAttendance absentRow = rows.Single(row => row.StudentId == absent.Id);
        Assert.True(absentRow.IsAbsent);
        Assert.False(absentRow.IsExcused);
        Assert.Equal(AttendanceSubmissionFixture.AbsentCode, absentRow.AttendCode);

        StudentAttendance excusedRow = rows.Single(row => row.StudentId == excused.Id);
        Assert.True(excusedRow.IsAbsent);
        Assert.True(excusedRow.IsExcused);
        Assert.Equal(AttendanceSubmissionFixture.ExcusedCode, excusedRow.AttendCode);

        StudentAttendance presentRow = rows.Single(row => row.StudentId == present.Id);
        Assert.False(presentRow.IsAbsent);
        Assert.False(presentRow.IsExcused);
        Assert.Equal(AttendanceSubmissionFixture.PresentCode, presentRow.AttendCode);
    }

    /// <summary>D-02 and V-23: four fields, snapshotted at save and never recomputed on read.</summary>
    [Fact]
    public async Task Handle_WhenCodeIsResolved_SnapshotsAllFourFields()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        await fixture.Handler().Handle(
            fixture.Command(
                AttendanceSubmissionFixture.Entry(student.Id, AttendanceSubmissionFixture.ExcusedCode)),
            CancellationToken.None);

        StudentAttendance row = (await Rows(fixture)).Single();

        Assert.Equal(AttendanceSubmissionFixture.ExcusedCode, row.AttendCode);
        Assert.Equal("Absent — excused", row.AttendCodeDescription);
        Assert.True(row.IsAbsent);
        Assert.True(row.IsExcused);
        Assert.Equal(fixture.Codes[AttendanceSubmissionFixture.ExcusedCode].Id, row.AttendanceCodeId);
    }

    /// <summary>"Write-once at save" is per save, not per row lifetime (F01d §6).</summary>
    [Fact]
    public async Task Handle_WhenRowIsUpdated_OverwritesTheSnapshotFromTheNewCode()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        await fixture.AddAttendanceAsync(student.Id, AttendanceSubmissionFixture.SubmittedDate);

        await fixture.Handler().Handle(
            fixture.Command(
                AttendanceSubmissionFixture.Entry(student.Id, AttendanceSubmissionFixture.PresentCode)),
            CancellationToken.None);

        StudentAttendance row = (await Rows(fixture)).Single();

        Assert.Equal(AttendanceSubmissionFixture.PresentCode, row.AttendCode);
        Assert.Equal("Present", row.AttendCodeDescription);
        Assert.False(row.IsAbsent);
    }

    /// <summary>
    ///     O-01, <b>both branches</b> — the update branch is the one that gets forgotten, and F11
    ///     enumerates a submission's rows through this column.
    /// </summary>
    [Fact]
    public async Task Handle_SetsSubmissionIdOnCreatedAndUpdatedRows()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();

        Student created = await fixture.AddStudentAsync();
        Student updated = await fixture.AddStudentAsync();

        await fixture.AddAttendanceAsync(updated.Id, AttendanceSubmissionFixture.SubmittedDate);

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(
                AttendanceSubmissionFixture.Entry(created.Id),
                AttendanceSubmissionFixture.Entry(updated.Id)),
            CancellationToken.None);

        Assert.All(
            await Rows(fixture),
            row => Assert.Equal(response.SubmissionId, row.SubmissionId));
    }

    [Fact]
    public async Task Handle_SetsTermIdFromTheResolvedTerm()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        domain.SchoolTerms.SchoolTerm term = await fixture.AddTermAsync(
            AttendanceSubmissionFixture.SubmittedDate.AddDays(-20),
            AttendanceSubmissionFixture.SubmittedDate.AddDays(20));

        await fixture.Handler().Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None);

        Assert.Equal(term.Id, (await Rows(fixture)).Single().TermId);
    }

    /// <summary>
    ///     <b>V-05's <c>Verified by</c>, at the behavioural level.</b> The analyzer covers the
    ///     prohibition half: <c>features</c> cannot reach a raw-SQL API at all (VC-01) and
    ///     <c>BannedSymbols.txt</c> catches the day someone adds the reference. This covers the other
    ///     half — the value is a parameter, so it is stored exactly as sent and does nothing.
    /// </summary>
    [Fact]
    public async Task Handle_WhenNotesContainSqlMetacharacters_StoresThemVerbatimAndEchoesNothing()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        const string Injection = "'; UPDATE students SET grade='X' --";

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(student.Id, notes: Injection)),
            CancellationToken.None);

        Assert.Equal(Injection, (await Rows(fixture)).Single().Notes);

        // Unchanged, which is what "the payload is data" means.
        await using infra.persistence.postgre.SparkrockRwcDbContext reader = fixture.NewContext();
        Assert.Equal("09", (await reader.Students.SingleAsync(row => row.Id == student.Id)).Grade);

        Assert.DoesNotContain(
            Injection,
            System.Text.Json.JsonSerializer.Serialize(response),
            StringComparison.Ordinal);
    }

    /// <summary>
    ///     The upsert has no delete branch. An omitted student is untouched (V-20), and a submitted one
    ///     is never removed — so nothing here can reach the soft-delete rewrite.
    /// </summary>
    [Fact]
    public async Task Handle_NeverCallsRemoveOnAnAttendanceRow()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        StudentAttendance existing = await fixture.AddAttendanceAsync(
            student.Id, AttendanceSubmissionFixture.SubmittedDate);

        await fixture.Handler().Handle(
            fixture.Command(
                AttendanceSubmissionFixture.Entry(student.Id, AttendanceSubmissionFixture.PresentCode)),
            CancellationToken.None);

        await using infra.persistence.postgre.SparkrockRwcDbContext reader = fixture.NewContext();

        StudentAttendance row = await reader.StudentAttendances.SingleAsync(candidate => candidate.Id == existing.Id);
        Assert.False(row.IsDeleted);
        Assert.Null(row.DeletedAt);
    }

    // ------------------------------------------------------------------- T07-10, summary rows

    [Fact]
    public async Task Handle_WhenNoSummaryExists_CreatesOneWithTheComputedTotal()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        await fixture.AddAttendanceAsync(student.Id, AttendanceSubmissionFixture.SubmittedDate.AddDays(-1));

        await fixture.Handler().Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None);

        StudentAttendanceSummary summary = await Summary(fixture, student.Id);

        Assert.Equal(2, summary.TotalAbsences);
        Assert.Equal(fixture.SchoolId, summary.SchoolId);
        Assert.Equal(AttendanceSubmissionFixture.SchoolYear, summary.SchoolYearStart);
    }

    /// <summary>F09 then never has to distinguish "no row" from "zero".</summary>
    [Fact]
    public async Task Handle_WhenNoSummaryExistsAndTotalIsZero_StillCreatesTheRow()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        await fixture.Handler().Handle(
            fixture.Command(
                AttendanceSubmissionFixture.Entry(student.Id, AttendanceSubmissionFixture.PresentCode)),
            CancellationToken.None);

        Assert.Equal(0, (await Summary(fixture, student.Id)).TotalAbsences);
    }

    [Fact]
    public async Task Handle_WhenSummaryExistsAndTotalChanged_UpdatesIt()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        await fixture.AddSummaryAsync(student.Id, totalAbsences: 0);

        await fixture.Handler().Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None);

        Assert.Equal(1, (await Summary(fixture, student.Id)).TotalAbsences);
    }

    /// <summary>
    ///     A resubmission that changes nothing must not stamp <c>ModifiedAt</c> or burn the <c>xmin</c>
    ///     token, which would manufacture contention on exactly the row the retry loop contends over.
    /// </summary>
    /// <remarks>
    ///     <b>This asserts the outcome, and it is not a guard on the handler's conditional.</b> Deleting
    ///     the <c>if (summary.TotalAbsences == total &amp;&amp; summary.SchoolId == …) return;</c> from
    ///     <c>UpsertSummary</c> leaves this green — verified by doing it — because assigning a tracked
    ///     entity the value it already holds leaves EF's change tracker reporting no modification, so no
    ///     <c>UPDATE</c> is emitted either way. That is the same mechanism that made
    ///     <c>Handle_WhenAlreadyInactive_DoesNotWrite</c> inert in three other features, and it is
    ///     recorded here rather than left to be discovered.
    ///     <para>
    ///         The explicit conditional is kept because it states the intent and because it is what
    ///         keeps the row out of the attempt's <c>UpdatedSummaries</c> bookkeeping — but no
    ///         behavioural test can distinguish its presence from its absence, so nothing in this suite
    ///         claims to.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task Handle_WhenSummaryExistsAndNothingChanged_DoesNotWriteIt()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        // Already absent today, and the summary already says 1. Resubmitting absent recomputes to 1.
        await fixture.AddAttendanceAsync(student.Id, AttendanceSubmissionFixture.SubmittedDate);
        await fixture.AddSummaryAsync(student.Id, totalAbsences: 1);

        fixture.Clock.Advance(TimeSpan.FromMinutes(5));

        await fixture.Handler().Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None);

        Assert.Null((await Summary(fixture, student.Id)).ModifiedAt);
    }

    /// <summary>
    ///     Closes F01d's R-6. DEC-08 guarantees the submitting school is the student's current one;
    ///     leaving <c>SchoolId</c> stale strands a transferred student on the former school's F09 list
    ///     for the rest of the year while never appearing on the receiving school's.
    /// </summary>
    [Fact]
    public async Task Handle_WhenStudentTransferredAndSubmits_UpdatesSummarySchoolOfRecord()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        Guid formerSchool = Guid.NewGuid();
        await fixture.AddSummaryAsync(student.Id, totalAbsences: 1, schoolId: formerSchool);

        await fixture.Handler().Handle(
            fixture.Command(
                AttendanceSubmissionFixture.Entry(student.Id, AttendanceSubmissionFixture.PresentCode)),
            CancellationToken.None);

        StudentAttendanceSummary summary = await Summary(fixture, student.Id);

        Assert.Equal(fixture.SchoolId, summary.SchoolId);

        // The total also fell to zero — the prior absence was at the former school, on a *different*
        // date, so it still counts (V-07c); there is none, so this is 0.
        Assert.Equal(0, summary.TotalAbsences);
    }

    /// <summary>One summary row per student per year, across schools — <c>SchoolId</c> is not in the key.</summary>
    [Fact]
    public async Task Handle_SummaryKeyIsStudentAndYearOnly_NotSchool()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        await fixture.AddSummaryAsync(student.Id, totalAbsences: 3, schoolId: Guid.NewGuid());

        await fixture.Handler().Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None);

        await using infra.persistence.postgre.SparkrockRwcDbContext reader = fixture.NewContext();

        Assert.Single(await reader.StudentAttendanceSummaries
            .Where(summary => summary.StudentId == student.Id)
            .ToListAsync());
    }

    // ------------------------------------------------------------------------------- helpers

    private static async Task<IReadOnlyList<StudentAttendance>> Rows(AttendanceSubmissionFixture fixture)
    {
        await using infra.persistence.postgre.SparkrockRwcDbContext reader = fixture.NewContext();

        return await reader.StudentAttendances.ToListAsync();
    }

    private static async Task<StudentAttendanceSummary> Summary(AttendanceSubmissionFixture fixture, Guid studentId)
    {
        await using infra.persistence.postgre.SparkrockRwcDbContext reader = fixture.NewContext();

        return await reader.StudentAttendanceSummaries.SingleAsync(summary => summary.StudentId == studentId);
    }

    private static async Task<Violation> SingleViolation(
        AttendanceSubmissionFixture fixture,
        SaveDailyAttendance.Entry entry)
    {
        BusinessRuleException exception = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            fixture.Handler().Handle(fixture.Command(entry), CancellationToken.None));

        Assert.Equal(ErrorCodes.Attendance.SubmissionRejected, exception.ErrorCode);

        return Assert.Single(exception.Violations);
    }
}
