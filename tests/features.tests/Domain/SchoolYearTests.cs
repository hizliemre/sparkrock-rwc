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
    public void Converter_RoundTripsThroughInt()
    {
        SchoolYear original = SchoolYear.FromLocalDate(new DateOnly(2026, 9, 1));

        object? stored = _converter.ConvertToProvider(original);
        object? restored = _converter.ConvertFromProvider(stored);

        Assert.Equal(2026, stored);
        Assert.Equal(original, restored);
    }
}
