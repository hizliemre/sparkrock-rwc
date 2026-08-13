using domain.ValueObjects;

namespace features.tests.Domain;

public sealed class SchoolYearFromLocalDateTests
{
    [Fact]
    public void FromLocalDate_WhenSeptemberFirst_StartsNewYear()
    {
        SchoolYear schoolYear = SchoolYear.FromLocalDate(new DateOnly(2026, 9, 1));

        Assert.Equal(2026, schoolYear.StartYear);
    }

    [Fact]
    public void FromLocalDate_WhenAugustThirtyFirst_StaysInPreviousYear()
    {
        SchoolYear schoolYear = SchoolYear.FromLocalDate(new DateOnly(2026, 8, 31));

        Assert.Equal(2025, schoolYear.StartYear);
    }

    [Fact]
    public void FromLocalDate_WhenLateInCalendarYear_UsesThatYear()
    {
        SchoolYear schoolYear = SchoolYear.FromLocalDate(new DateOnly(2026, 12, 31));

        Assert.Equal(2026, schoolYear.StartYear);
    }

    [Fact]
    public void FromLocalDate_WhenEarlyInCalendarYear_UsesPreviousYear()
    {
        SchoolYear schoolYear = SchoolYear.FromLocalDate(new DateOnly(2026, 1, 1));

        Assert.Equal(2025, schoolYear.StartYear);
    }
}

public sealed class SchoolYearParsingTests
{
    [Fact]
    public void ToString_RendersHyphenatedRange()
    {
        SchoolYear schoolYear = SchoolYear.FromLocalDate(new DateOnly(2026, 9, 1));

        Assert.Equal("2026-2027", schoolYear.ToString());
    }

    [Fact]
    public void TryParse_WhenWellFormed_Succeeds()
    {
        bool parsed = SchoolYear.TryParse("2026-2027", out SchoolYear schoolYear);

        Assert.True(parsed);
        Assert.Equal(2026, schoolYear.StartYear);
    }

    [Fact]
    public void TryParse_WhenYearsNotConsecutive_Fails()
    {
        bool parsed = SchoolYear.TryParse("2026-2028", out SchoolYear schoolYear);

        Assert.False(parsed);
        Assert.Equal(default, schoolYear);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("2026")]
    [InlineData("abcd-efgh")]
    [InlineData("2026-")]
    [InlineData("-2027")]
    [InlineData("2026-2027-2028")]
    public void TryParse_WhenMalformed_Fails(string? value)
    {
        bool parsed = SchoolYear.TryParse(value, out SchoolYear schoolYear);

        Assert.False(parsed);
        Assert.Equal(default, schoolYear);
    }

    [Fact]
    public void TryParse_WhenStartYearNotPositive_Fails()
    {
        bool parsed = SchoolYear.TryParse("0-1", out SchoolYear schoolYear);

        Assert.False(parsed);
        Assert.Equal(default, schoolYear);
    }

    [Theory]
    [InlineData("1899-1900")]
    [InlineData("2101-2102")]
    [InlineData("9999-10000")]
    [InlineData("2147483646-2147483647")]
    public void TryParse_WhenOutsideSupportedRange_Fails(string value)
    {
        bool parsed = SchoolYear.TryParse(value, out SchoolYear schoolYear);

        Assert.False(parsed);
        Assert.Equal(default, schoolYear);
    }

    [Theory]
    [InlineData(" 2026-2027")]
    [InlineData("2026-2027 ")]
    [InlineData("2026 - 2027")]
    [InlineData("\t2026-2027\n")]
    [InlineData("+2026-+2027")]
    [InlineData("0002026-0002027")]
    public void TryParse_WhenNotCanonicalForm_Fails(string value)
    {
        bool parsed = SchoolYear.TryParse(value, out SchoolYear schoolYear);

        Assert.False(parsed);
        Assert.Equal(default, schoolYear);
    }

    [Fact]
    public void TryParse_RoundTripsThroughToString()
    {
        SchoolYear original = SchoolYear.FromLocalDate(new DateOnly(2026, 9, 1));

        Assert.True(SchoolYear.TryParse(original.ToString(), out SchoolYear restored));
        Assert.Equal(original, restored);
    }
}

public sealed class SchoolYearRangeGuardTests
{
    [Theory]
    [InlineData(1899)]
    [InlineData(2101)]
    [InlineData(0)]
    [InlineData(-5)]
    public void FromStartYear_WhenOutsideSupportedRange_Throws(int startYear)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SchoolYear.FromStartYear(startYear));
    }

    [Theory]
    [InlineData(1900)]
    [InlineData(2100)]
    public void FromStartYear_AtRangeBoundary_Succeeds(int startYear)
    {
        Assert.Equal(startYear, SchoolYear.FromStartYear(startYear).StartYear);
    }

    [Fact]
    public void FromLocalDate_WhenBeforeSupportedRange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SchoolYear.FromLocalDate(DateOnly.MinValue));
    }

    [Fact]
    public void FromLocalDate_WhenAfterSupportedRange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SchoolYear.FromLocalDate(DateOnly.MaxValue));
    }

    [Fact]
    public void ToDateRange_NeverThrowsForAnyConstructibleValue()
    {
        for (int startYear = SchoolYear.MinStartYear; startYear <= SchoolYear.MaxStartYear; startYear++)
        {
            (DateOnly from, DateOnly toExclusive) = SchoolYear.FromStartYear(startYear).ToDateRange();

            Assert.True(from < toExclusive);
        }
    }

    [Fact]
    public void ToDateRange_WhenDefault_ThrowsDiagnosably()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => default(SchoolYear).ToDateRange());

        Assert.Contains(nameof(SchoolYear.FromStartYear), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToDateRange_WhenFailedParseOutValueIsUsed_ThrowsDiagnosably()
    {
        Assert.False(SchoolYear.TryParse("not-a-year", out SchoolYear schoolYear));

        Assert.Throws<InvalidOperationException>(() => schoolYear.ToDateRange());
    }
}

public sealed class SchoolYearDateRangeTests
{
    [Fact]
    public void ToDateRange_StartsOnSeptemberFirst()
    {
        SchoolYear schoolYear = SchoolYear.FromLocalDate(new DateOnly(2026, 11, 4));

        (DateOnly from, DateOnly _) = schoolYear.ToDateRange();

        Assert.Equal(new DateOnly(2026, 9, 1), from);
    }

    [Fact]
    public void ToDateRange_EndsExclusiveOnNextSeptemberFirst()
    {
        SchoolYear schoolYear = SchoolYear.FromLocalDate(new DateOnly(2026, 11, 4));

        (DateOnly _, DateOnly toExclusive) = schoolYear.ToDateRange();

        Assert.Equal(new DateOnly(2027, 9, 1), toExclusive);
    }

    [Fact]
    public void ToDateRange_RoundTripsThroughFromLocalDate()
    {
        SchoolYear schoolYear = SchoolYear.FromLocalDate(new DateOnly(2026, 11, 4));

        (DateOnly from, DateOnly toExclusive) = schoolYear.ToDateRange();

        Assert.Equal(schoolYear, SchoolYear.FromLocalDate(from));
        Assert.Equal(schoolYear.StartYear + 1, SchoolYear.FromLocalDate(toExclusive).StartYear);
    }

    [Fact]
    public void ToDateRange_ExcludesTheDayBeforeItStarts()
    {
        SchoolYear schoolYear = SchoolYear.FromLocalDate(new DateOnly(2026, 11, 4));

        (DateOnly from, DateOnly toExclusive) = schoolYear.ToDateRange();

        Assert.NotEqual(schoolYear, SchoolYear.FromLocalDate(from.AddDays(-1)));
        Assert.NotEqual(schoolYear, SchoolYear.FromLocalDate(toExclusive));
    }
}

public sealed class SchoolYearToIntConverterTests
{
    private readonly SchoolYearToIntConverter _converter = new();

    [Fact]
    public void ConvertToProvider_RoundTripsThroughInt()
    {
        SchoolYear original = SchoolYear.FromLocalDate(new DateOnly(2026, 9, 1));

        object? stored = _converter.ConvertToProvider(original);
        object? restored = _converter.ConvertFromProvider(stored);

        Assert.Equal(2026, stored);
        Assert.Equal(original, restored);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(int.MaxValue)]
    public void ConvertFromProvider_WhenValueOutOfRange_Throws(int stored)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _converter.ConvertFromProvider(stored));
    }
}
