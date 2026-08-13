using domain.Exceptions;

namespace features.tests.Exceptions;

public sealed class DomainExceptionTests
{
    [Fact]
    public void BusinessRuleException_ExposesErrorCodeAndViolations()
    {
        Violation[] violations =
        [
            new("body", "entries[3].attendCode", ErrorCodes.Validation.RequiredField, "Code is required."),
            new("body", "entries[7].studentId", ErrorCodes.Validation.RequiredField, "Student is required.")
        ];

        BusinessRuleException exception = new(ErrorCodes.Validation.Failed, violations);

        Assert.Equal(ErrorCodes.Validation.Failed, exception.ErrorCode);
        Assert.Equal(2, exception.Violations.Count);
        Assert.Equal("entries[3].attendCode", exception.Violations[0].Path);
    }

    /// <summary>
    ///     conventions §2's existence-oracle rule, made structural: the type takes no message, so no
    ///     call site can make "does not exist" and "not yours" distinguishable.
    /// </summary>
    [Fact]
    public void NotFoundException_MessageIsIdenticalAcrossErrorCodes()
    {
        NotFoundException missing = new("SCHOOL.NOT_FOUND");
        NotFoundException outOfScope = new("STUDENT.NOT_FOUND");

        Assert.Equal(missing.Message, outOfScope.Message);
        Assert.Equal(NotFoundException.NotFoundMessage, missing.Message);
        Assert.NotEqual(missing.ErrorCode, outOfScope.ErrorCode);
    }

    [Fact]
    public void ConflictException_ExposesErrorCodeAndMessage()
    {
        ConflictException exception = new("SCHOOL.INACTIVE", "The school is inactive.");

        Assert.Equal("SCHOOL.INACTIVE", exception.ErrorCode);
        Assert.Equal("The school is inactive.", exception.Message);
    }

    [Fact]
    public void ForbiddenException_ExposesErrorCodeAndMessage()
    {
        ForbiddenException exception = new(ErrorCodes.System.Forbidden, "Requires a system administrator.");

        Assert.Equal(ErrorCodes.System.Forbidden, exception.ErrorCode);
        Assert.Equal("Requires a system administrator.", exception.Message);
    }

    /// <summary>
    ///     DEC-14's retry predicate must tell retryable from permanent. Inheritance would let one
    ///     <c>catch (ConflictException)</c> swallow the retryable case and lose the whole submission.
    /// </summary>
    [Fact]
    public void ConcurrencyConflictException_IsNotAConflictException()
    {
        Assert.False(typeof(ConflictException).IsAssignableFrom(typeof(ConcurrencyConflictException)));
    }

    [Fact]
    public void ConcurrencyConflictException_ExposesConstraintNameAndErrorCode()
    {
        Microsoft.EntityFrameworkCore.DbUpdateException inner = new("duplicate key");

        ConcurrencyConflictException exception = new(
            "ix_student_attendance_summaries_student_id_school_year_start",
            "ATTENDANCE.CONCURRENT_SUBMISSION",
            "Another submission updated this student.",
            inner);

        Assert.Equal("ix_student_attendance_summaries_student_id_school_year_start", exception.ConstraintName);
        Assert.Equal("ATTENDANCE.CONCURRENT_SUBMISSION", exception.ErrorCode);
        Assert.Same(inner, exception.InnerException);
    }

    /// <summary>
    ///     VC-29: <c>ex.Entries</c> is the only route from <c>features</c> to the recovery F07 needs.
    ///     Discarding them when translating would leave the retry unable to converge.
    /// </summary>
    [Fact]
    public void ConcurrencyConflictException_CarriesEntriesFromTheDbUpdateException()
    {
        Microsoft.EntityFrameworkCore.DbUpdateException inner = new("duplicate key");

        ConcurrencyConflictException exception = new("ix_any", "ANY.CODE", "message", inner);

        Assert.Equal(inner.Entries.Count, exception.Entries.Count);
    }
}
