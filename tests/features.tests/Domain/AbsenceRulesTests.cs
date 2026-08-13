using domain.Attendance;

namespace features.tests.Domain;

public sealed class AbsenceRulesTests
{
    [Fact]
    public void ResolveThreshold_WhenSchoolThresholdNull_ReturnsDefaultOfTen()
    {
        Assert.Equal(10, AbsenceRules.ResolveThreshold(null));
    }

    [Fact]
    public void ResolveThreshold_WhenSchoolThresholdSet_ReturnsIt()
    {
        Assert.Equal(25, AbsenceRules.ResolveThreshold(25));
    }

    [Fact]
    public void IsChronicallyAbsent_WhenBelowThreshold_ReturnsFalse()
    {
        Assert.False(AbsenceRules.IsChronicallyAbsent(9, 10));
    }

    [Fact]
    public void IsChronicallyAbsent_WhenAtThreshold_ReturnsTrue()
    {
        Assert.True(AbsenceRules.IsChronicallyAbsent(10, 10));
    }

    [Fact]
    public void IsChronicallyAbsent_WhenAboveThreshold_ReturnsTrue()
    {
        Assert.True(AbsenceRules.IsChronicallyAbsent(11, 10));
    }

    [Fact]
    public void IsChronicallyAbsent_WhenNullThreshold_UsesDefault()
    {
        Assert.False(AbsenceRules.IsChronicallyAbsent(9, null));
        Assert.True(AbsenceRules.IsChronicallyAbsent(10, null));
    }
}
