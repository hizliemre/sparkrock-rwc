using domain.Alerts;

namespace features.tests.Domain;

public sealed class AlertRulesRaiseTests
{
    [Fact]
    public void ShouldRaise_WhenChronicAndNoOpenEpisode_ReturnsTrue()
    {
        Assert.True(AlertRules.ShouldRaise(10, 10, hasOpenEpisode: false, hasManualResolutionThisYear: false));
    }

    [Fact]
    public void ShouldRaise_WhenChronicAndEpisodeAlreadyOpen_ReturnsFalse()
    {
        Assert.False(AlertRules.ShouldRaise(10, 10, hasOpenEpisode: true, hasManualResolutionThisYear: false));
    }

    [Fact]
    public void ShouldRaise_WhenBelowThreshold_ReturnsFalse()
    {
        Assert.False(AlertRules.ShouldRaise(9, 10, hasOpenEpisode: false, hasManualResolutionThisYear: false));
    }

    [Fact]
    public void ShouldRaise_WithDistinctCountAndThreshold_ReadsArgumentsInOrder()
    {
        Assert.False(AlertRules.ShouldRaise(3, 17, hasOpenEpisode: false, hasManualResolutionThisYear: false));
        Assert.True(AlertRules.ShouldRaise(17, 3, hasOpenEpisode: false, hasManualResolutionThisYear: false));
    }

    [Fact]
    public void ShouldRaise_WhenNullThreshold_UsesDefault()
    {
        Assert.True(AlertRules.ShouldRaise(10, null, hasOpenEpisode: false, hasManualResolutionThisYear: false));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(500)]
    public void ShouldRaise_WhenManuallyResolvedThisYear_ReturnsFalse(int totalAbsences)
    {
        Assert.False(AlertRules.ShouldRaise(totalAbsences, 10, hasOpenEpisode: false, hasManualResolutionThisYear: true));
    }
}

public sealed class AlertRulesAutoResolveTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(9)]
    public void ShouldAutoResolve_WhenBelowThreshold_ReturnsTrue(int totalAbsences)
    {
        Assert.True(AlertRules.ShouldAutoResolve(totalAbsences, 10, hasOpenEpisode: true));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public void ShouldAutoResolve_WhenAtOrAboveThreshold_ReturnsFalse(int totalAbsences)
    {
        Assert.False(AlertRules.ShouldAutoResolve(totalAbsences, 10, hasOpenEpisode: true));
    }

    [Fact]
    public void ShouldAutoResolve_WhenNoOpenEpisode_ReturnsFalse()
    {
        Assert.False(AlertRules.ShouldAutoResolve(0, 10, hasOpenEpisode: false));
    }

    [Fact]
    public void ShouldRaiseAndShouldAutoResolve_AreNeverBothTrue()
    {
        for (int totalAbsences = 0; totalAbsences <= 20; totalAbsences++)
        {
            bool raise = AlertRules.ShouldRaise(totalAbsences, 10, hasOpenEpisode: false, hasManualResolutionThisYear: false);
            bool resolve = AlertRules.ShouldAutoResolve(totalAbsences, 10, hasOpenEpisode: true);

            Assert.False(raise && resolve);
        }
    }
}
