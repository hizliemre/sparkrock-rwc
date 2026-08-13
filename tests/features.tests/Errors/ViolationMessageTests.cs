using api.Errors;

namespace features.tests.Errors;

/// <summary>
///     The last thing between a validator's default English and the response body.
/// </summary>
/// <remarks>
///     Conventions §2: messages may echo bounded structured values (a code, an index) but never
///     free-text fields, and <c>Notes</c> never appears in a response body. Several FluentValidation
///     built-ins interpolate <c>{PropertyValue}</c>, so a <c>MaximumLength</c> rule on <c>Notes</c>
///     ships the safeguarding text straight back to the caller — and the caller on a 400 is whoever
///     sent the request, not necessarily whoever is entitled to read it.
/// </remarks>
public sealed class ViolationMessageTests
{
    private const string SafeguardingText =
        "Mother reports the child is being kept home following the incident on Tuesday.";

    [Fact]
    public void Sanitise_WhenThePropertyIsFreeText_ReplacesTheWholeMessage()
    {
        string sanitised = ViolationMessage.Sanitise(
            $"'Notes' must be 64 characters or fewer. You entered '{SafeguardingText}'.",
            "Entries[3].Notes",
            SafeguardingText);

        Assert.DoesNotContain("Mother reports", sanitised, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(sanitised));
    }

    [Theory]
    [InlineData("Notes")]
    [InlineData("notes")]
    [InlineData("Comment")]
    [InlineData("Comments")]
    [InlineData("Description")]
    [InlineData("Reason")]
    [InlineData("Entries[7].Notes")]
    public void Sanitise_WhenTheLeafSegmentNamesAFreeTextField_ReplacesTheWholeMessage(string clrPath)
    {
        Assert.Equal(
            ViolationMessage.Redacted,
            ViolationMessage.Sanitise($"You entered '{SafeguardingText}'.", clrPath, SafeguardingText));
    }

    /// <summary>
    ///     The name list cannot be complete. A value too long to be a code or an index is free text
    ///     whatever the field is called, so it is redacted from wherever it appears.
    /// </summary>
    [Fact]
    public void Sanitise_WhenTheAttemptedValueIsLongerThanABoundedValue_RedactsIt()
    {
        string sanitised = ViolationMessage.Sanitise(
            $"'Test Property' must be 10 characters or fewer. You entered '{SafeguardingText}'.",
            "TestProperty",
            SafeguardingText);

        Assert.DoesNotContain(SafeguardingText, sanitised, StringComparison.Ordinal);
        Assert.Contains("must be 10 characters or fewer", sanitised, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Conventions permit a bounded structured value. Redacting an attendance code would remove the
    ///     one thing that makes the message useful to the developer reading it.
    /// </summary>
    [Fact]
    public void Sanitise_WhenTheAttemptedValueIsBounded_LeavesItAlone()
    {
        const string message = "Attendance code 'XX' does not exist or is inactive.";

        Assert.Equal(message, ViolationMessage.Sanitise(message, "Entries[3].AttendCode", "XX"));
    }

    [Fact]
    public void Sanitise_WhenTheAttemptedValueIsNotAString_LeavesTheMessageAlone()
    {
        const string message = "'Page Size' must be 200 or fewer. You entered 5000.";

        Assert.Equal(message, ViolationMessage.Sanitise(message, "PageSize", 5000));
    }

    [Fact]
    public void Sanitise_WhenTheMessageIsMissing_ReturnsTheRedactionPlaceholder()
    {
        Assert.Equal(ViolationMessage.Redacted, ViolationMessage.Sanitise(null, "TestProperty", null));
        Assert.Equal(ViolationMessage.Redacted, ViolationMessage.Sanitise("   ", "TestProperty", null));
    }

    /// <summary>
    ///     An unbounded message is itself a leak channel even when no attempted value is quoted.
    /// </summary>
    [Fact]
    public void Sanitise_WhenTheMessageIsOverlong_Truncates()
    {
        string sanitised = ViolationMessage.Sanitise(new string('x', 5000), "TestProperty", null);

        Assert.True(sanitised.Length <= ViolationMessage.MaximumMessageLength);
    }
}
