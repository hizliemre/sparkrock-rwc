using api.Errors;
using domain.Attendance;
using domain.Exceptions;
using features.Attendance;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;

namespace features.tests.Attendance;

/// <summary>
///     Stage A of spec §2 — the shape checks, which run before any database work.
/// </summary>
/// <remarks>
///     Handler tier with no provider at all: <c>CommandValidator</c> references no entity, so these
///     were startable against the kernel alone.
///     <para>
///         <c>IdempotencyKey</c> is deliberately <b>not</b> a validator rule. <c>ViolationSource</c>
///         documents that <c>header</c> is never inferred, so a validator failure on it would be
///         reported as <c>"source": "body"</c> for a value that was never in the body — precisely the
///         lie that helper exists to remove. The bound is enforced in the handler with a
///         hand-constructed <see cref="Violation" />; see
///         <c>SaveDailyAttendanceHandlerTests.Handle_WhenIdempotencyKeyExceedsSixtyFourCharacters_ThrowsBusinessRuleExceptionWithHeaderSource</c>.
///     </para>
/// </remarks>
public sealed class SaveDailyAttendanceValidatorTests
{
    private const string WellFormedDate = "2026-09-14";

    [Fact]
    public void Validate_WhenEntriesAreEmpty_Fails()
    {
        ValidationResult result = Validate(Command([]));

        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal(ErrorCodes.Validation.RequiredField, failure.ErrorCode);
        Assert.Equal(nameof(SaveDailyAttendance.Command.Entries), failure.PropertyName);
    }

    [Fact]
    public void Validate_WhenEntryCountExceedsMaxBatchSize_Fails()
    {
        ValidationResult result = Validate(Command(Entries(AttendanceSave.MaxBatchSize + 1)));

        Assert.Contains(result.Errors, failure => failure.ErrorCode == ErrorCodes.Attendance.BatchSizeExceeded);
    }

    /// <summary>The off-by-one: exactly <see cref="AttendanceSave.MaxBatchSize" /> is accepted.</summary>
    [Fact]
    public void Validate_WhenEntryCountEqualsMaxBatchSize_Succeeds()
    {
        ValidationResult result = Validate(Command(Entries(AttendanceSave.MaxBatchSize)));

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(error => error.ErrorMessage)));
    }

    /// <summary>
    ///     <b>V-15's <c>Verified by</c>.</b>
    /// </summary>
    /// <remarks>
    ///     A validator rule rather than a handler rule for two reasons: the <c>+1</c> would be applied
    ///     twice to one total, and EF would hold two <c>Added</c> rows for one
    ///     <c>(StudentId, AttendDate)</c> — a <c>23505</c> the retry loop would then treat as a race and
    ///     retry twice before failing. Rejecting it at the boundary is cheaper and honest: the payload
    ///     is wrong.
    /// </remarks>
    [Fact]
    public void Validate_WhenStudentIdAppearsTwice_Fails()
    {
        Guid repeated = Guid.NewGuid();

        ValidationResult result = Validate(Command(
        [
            Entry(repeated),
            Entry(Guid.NewGuid()),
            Entry(repeated)
        ]));

        ValidationFailure failure = Assert.Single(result.Errors, error => error.ErrorCode == ErrorCodes.Attendance.DuplicateStudent);

        // One violation per occurrence *after* the first, so the path names the repeat rather than
        // the original — the client's grid has two rows for one student and this says which to remove.
        Assert.Equal("Entries[2].StudentId", failure.PropertyName);
    }

    [Fact]
    public void Validate_WhenStudentIdAppearsThreeTimes_ReportsEachRepeat()
    {
        Guid repeated = Guid.NewGuid();

        ValidationResult result = Validate(Command([Entry(repeated), Entry(repeated), Entry(repeated)]));

        Assert.Equal(
            ["Entries[1].StudentId", "Entries[2].StudentId"],
            result.Errors
                .Where(error => error.ErrorCode == ErrorCodes.Attendance.DuplicateStudent)
                .Select(error => error.PropertyName));
    }

    [Fact]
    public void Validate_WhenStudentIdIsEmptyGuid_Fails()
    {
        ValidationResult result = Validate(Command([Entry(Guid.Empty)]));

        ValidationFailure failure = Assert.Single(result.Errors, error => error.PropertyName == "Entries[0].StudentId");

        Assert.Equal(ErrorCodes.Validation.RequiredField, failure.ErrorCode);
    }

    [Fact]
    public void Validate_WhenAttendCodeIsEmpty_Fails()
    {
        ValidationResult result = Validate(Command([Entry(Guid.NewGuid(), attendCode: "  ")]));

        ValidationFailure failure = Assert.Single(result.Errors, error => error.PropertyName == "Entries[0].AttendCode");

        Assert.Equal(ErrorCodes.Validation.RequiredField, failure.ErrorCode);
    }

    [Fact]
    public void Validate_WhenAttendCodeExceedsFiveCharacters_Fails()
    {
        ValidationResult result = Validate(Command([Entry(Guid.NewGuid(), attendCode: "ABCDEF")]));

        ValidationFailure failure = Assert.Single(result.Errors, error => error.PropertyName == "Entries[0].AttendCode");

        Assert.Equal(ErrorCodes.Validation.Failed, failure.ErrorCode);
    }

    /// <summary>
    ///     No shape rule beyond length. A typo and an unknown code must read the same — a pattern rule
    ///     here would be a second way to say "no such code", and the two would drift.
    /// </summary>
    [Fact]
    public void Validate_WhenAttendCodeIsLowerCase_Succeeds()
    {
        ValidationResult result = Validate(Command([Entry(Guid.NewGuid(), attendCode: "a")]));

        Assert.True(result.IsValid);
    }

    /// <summary>Mirrors <c>ck_student_attendances_minutes_late_not_negative</c>.</summary>
    [Fact]
    public void Validate_WhenMinutesLateIsNegative_Fails()
    {
        ValidationResult result = Validate(Command([Entry(Guid.NewGuid(), minutesLate: -1)]));

        ValidationFailure failure = Assert.Single(result.Errors, error => error.PropertyName == "Entries[0].MinutesLate");

        Assert.Equal(ErrorCodes.Validation.Failed, failure.ErrorCode);
    }

    [Fact]
    public void Validate_WhenMinutesLateIsZero_Succeeds()
    {
        Assert.True(Validate(Command([Entry(Guid.NewGuid(), minutesLate: 0)])).IsValid);
    }

    /// <summary>
    ///     The value must not reach the message. <c>Notes</c> routinely carries health and safeguarding
    ///     detail, and a 400 goes back to whoever sent the request.
    /// </summary>
    [Fact]
    public void Validate_WhenNotesExceedFiveHundredCharacters_Fails()
    {
        string notes = new('x', AttendanceSave.MaxNotesLength + 1);

        ValidationResult result = Validate(Command([Entry(Guid.NewGuid(), notes: notes)]));

        ValidationFailure failure = Assert.Single(result.Errors, error => error.PropertyName == "Entries[0].Notes");

        Assert.Equal(ErrorCodes.Validation.Failed, failure.ErrorCode);
        Assert.DoesNotContain(notes, failure.ErrorMessage, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Conventions §2: <c>{date}</c> binds as a string and is validated, so a malformed date is a
    ///     400 rather than a routing 404 indistinguishable from an unknown school.
    /// </summary>
    [Theory]
    [InlineData("14/09/2026")]
    [InlineData("2026-9-14")]
    [InlineData("2026/09/14")]
    [InlineData("2026-13-01")]
    [InlineData("")]
    public void Validate_WhenDateIsNotIso8601_Fails(string date)
    {
        ValidationResult result = Validate(Command([Entry(Guid.NewGuid())], date));

        ValidationFailure failure = Assert.Single(result.Errors, error => error.PropertyName == nameof(SaveDailyAttendance.Command.Date));

        Assert.Equal(ErrorCodes.Validation.Failed, failure.ErrorCode);
    }

    [Fact]
    public void Validate_WhenDateIsWellFormed_Succeeds()
    {
        Assert.True(Validate(Command([Entry(Guid.NewGuid())])).IsValid);
    }

    /// <summary>
    ///     The rule the validator deliberately does not own. If this starts failing, someone has moved
    ///     the length bound into FluentValidation and the response now claims a header value came from
    ///     the body.
    /// </summary>
    [Fact]
    public void Validate_WhenIdempotencyKeyIsTooLong_ProducesNoValidatorFailure()
    {
        SaveDailyAttendance.Command command = new()
        {
            SchoolId = Guid.NewGuid(),
            Date = WellFormedDate,
            IdempotencyKey = new string('k', AttendanceSave.MaxIdempotencyKeyLength + 1),
            Entries = [Entry(Guid.NewGuid())]
        };

        Assert.True(Validate(command).IsValid);
    }

    /// <summary>
    ///     <b>F07 is the first endpoint to exercise <c>ViolationSource</c>'s <c>path</c> branch.</b>
    /// </summary>
    /// <remarks>
    ///     <c>ViolationSource.For</c> checks route values before query keys, and
    ///     <c>RouteValueDictionary</c> is case-insensitive, so the CLR root segment <c>Date</c> matches
    ///     the <c>{date}</c> route value. Asserted rather than assumed: the property name is
    ///     load-bearing, and renaming it to <c>AttendDate</c> would report a malformed path segment as
    ///     a body failure.
    /// </remarks>
    [Fact]
    public void ViolationSource_ForTheDateProperty_ReturnsPath()
    {
        DefaultHttpContext context = new();
        context.Request.RouteValues["schoolId"] = Guid.NewGuid().ToString();
        context.Request.RouteValues["date"] = "2026-13-01";
        context.Request.ContentType = "application/json";

        Assert.Equal(
            ViolationSource.Path,
            ViolationSource.For(context.Request, nameof(SaveDailyAttendance.Command.Date)));
    }

    /// <summary>
    ///     An entry-level failure is about the body even though the request also carries route values.
    /// </summary>
    [Fact]
    public void ViolationSource_ForAnEntryProperty_ReturnsBody()
    {
        DefaultHttpContext context = new();
        context.Request.RouteValues["schoolId"] = Guid.NewGuid().ToString();
        context.Request.RouteValues["date"] = "2026-09-14";
        context.Request.ContentType = "application/json";

        Assert.Equal(ViolationSource.Body, ViolationSource.For(context.Request, "Entries[0].AttendCode"));
    }

    /// <summary>
    ///     <c>Notes</c> must be in <c>ViolationMessage</c>'s authoritative name list, or a plain
    ///     <c>MaximumLength</c> rule returns safeguarding text to whoever sent the request. F07's own
    ///     messages never interpolate a value; this covers the validator path, which does not go
    ///     through them.
    /// </summary>
    [Fact]
    public void ViolationMessage_RedactsAMessageAboutNotes()
    {
        string secret = "Child disclosed abuse at home; social worker informed.";

        Assert.Equal(
            ViolationMessage.Redacted,
            ViolationMessage.Sanitise($"'{secret}' is too long.", "Entries[0].Notes", secret));
    }

    // ------------------------------------------------------------------------------------ helpers

    private static ValidationResult Validate(SaveDailyAttendance.Command command) =>
        new SaveDailyAttendance.CommandValidator().Validate(command);

    private static SaveDailyAttendance.Command Command(
        IReadOnlyList<SaveDailyAttendance.Entry> entries,
        string date = WellFormedDate) =>
        new()
        {
            SchoolId = Guid.NewGuid(),
            Date = date,
            Entries = entries
        };

    private static SaveDailyAttendance.Entry Entry(
        Guid studentId,
        string attendCode = "A",
        int? minutesLate = null,
        string? notes = null) =>
        new()
        {
            StudentId = studentId,
            AttendCode = attendCode,
            MinutesLate = minutesLate,
            Notes = notes
        };

    private static SaveDailyAttendance.Entry[] Entries(int count) =>
        Enumerable.Range(0, count).Select(_ => Entry(Guid.NewGuid())).ToArray();
}
