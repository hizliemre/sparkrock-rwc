using System.Globalization;
using features.AttendanceCodes;

namespace features.tests.AttendanceCodes;

/// <summary>
///     V-27's write-boundary half. SQL Server's default collation treated <c>A</c> and <c>a</c> as one
///     code; a Postgres unique index does not, so normalisation is what keeps the two the same code.
/// </summary>
public sealed class AttendanceCodeValueTests
{
    [Fact]
    public void Normalise_UpperCasesTheValue() => Assert.Equal("A", AttendanceCodeValue.Normalise("a"));

    [Fact]
    public void Normalise_TrimsSurroundingWhitespace() => Assert.Equal("A", AttendanceCodeValue.Normalise("  a  "));

    [Fact]
    public void Normalise_LeavesAnAlreadyNormalisedValueUnchanged() =>
        Assert.Equal("EX", AttendanceCodeValue.Normalise("EX"));

    /// <summary>
    ///     Under <c>ToUpper()</c> this passes on CI and fails on a Turkish-locale machine, then writes
    ///     a value <c>ck_attendance_codes_value_upper</c> rejects. The test exists because the failure
    ///     is machine-dependent and therefore invisible to everyone but the one developer who has it.
    /// </summary>
    [Fact]
    public void Normalise_UsesInvariantCulture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

            Assert.Equal("I", AttendanceCodeValue.Normalise("i"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>The validator produces the 400; the normaliser must not throw first.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalise_WhenNullOrWhitespace_ReturnsEmpty(string? value) =>
        Assert.Equal(string.Empty, AttendanceCodeValue.Normalise(value));
}
