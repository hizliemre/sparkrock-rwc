using api.Errors;
using domain.Exceptions;

namespace features.tests.Errors;

public sealed class ProblemDetailsDefaultsTests
{
    [Theory]
    [InlineData("ATTENDANCE.SUBMISSION_REJECTED", "https://sparkrock.example/errors/attendance-submission-rejected")]
    [InlineData("SYSTEM.NOT_FOUND", "https://sparkrock.example/errors/system-not-found")]
    [InlineData("VALIDATION.FAILED", "https://sparkrock.example/errors/validation-failed")]
    public void ToTypeUri_LowercasesAndHyphenates(string errorCode, string expected)
    {
        Assert.Equal(expected, ProblemDetailsDefaults.ToTypeUri(errorCode));
    }

    /// <summary>
    ///     Per status, never per handler — otherwise the same 400 gets a different title depending on
    ///     which code path produced it.
    /// </summary>
    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(500)]
    public void TitleFor_ReturnsANonEmptyTitleForEveryMappedStatus(int status)
    {
        Assert.False(string.IsNullOrWhiteSpace(ProblemDetailsDefaults.TitleFor(status)));
    }

    [Theory]
    [InlineData(400, "SYSTEM.MALFORMED_REQUEST")]
    [InlineData(403, "SYSTEM.FORBIDDEN")]
    [InlineData(404, "SYSTEM.NOT_FOUND")]
    [InlineData(405, "SYSTEM.METHOD_NOT_ALLOWED")]
    [InlineData(415, "SYSTEM.UNSUPPORTED_MEDIA_TYPE")]
    [InlineData(500, "SYSTEM.UNEXPECTED")]
    [InlineData(503, "SYSTEM.UNEXPECTED")]
    public void DefaultErrorCodeFor_MatchesTheStatusTable(int status, string expected)
    {
        Assert.Equal(expected, ProblemDetailsDefaults.DefaultErrorCodeFor(status));
    }

    [Fact]
    public void DefaultErrorCodeFor_UsesTheDeclaredConstants()
    {
        Assert.Equal(ErrorCodes.System.NotFound, ProblemDetailsDefaults.DefaultErrorCodeFor(404));
        Assert.Equal(ErrorCodes.System.Unexpected, ProblemDetailsDefaults.DefaultErrorCodeFor(500));
    }
}
