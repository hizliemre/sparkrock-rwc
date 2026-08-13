using domain.Alerts;

namespace features.tests.Domain;

public sealed class AlertRulesRaiseTests
{
    [Fact]
    public void ShouldRaise_WhenChronicAndNoOpenEpisode_ReturnsTrue()
    {
        Assert.True(AlertRules.ShouldRaise(10, 10, hasOpenEpisode: false));
    }

    [Fact]
    public void ShouldRaise_WhenChronicAndEpisodeAlreadyOpen_ReturnsFalse()
    {
        Assert.False(AlertRules.ShouldRaise(10, 10, hasOpenEpisode: true));
    }

    [Fact]
    public void ShouldRaise_WhenBelowThreshold_ReturnsFalse()
    {
        Assert.False(AlertRules.ShouldRaise(9, 10, hasOpenEpisode: false));
    }

    [Fact]
    public void ShouldRaise_WhenNullThreshold_UsesDefault()
    {
        Assert.True(AlertRules.ShouldRaise(10, null, hasOpenEpisode: false));
    }
}

public sealed class AlertRulesAutoResolveTests
{
    [Fact]
    public void ShouldAutoResolve_WhenBelowThreshold_ReturnsTrue()
    {
        Assert.True(AlertRules.ShouldAutoResolve(9, 10, existingResolution: null));
    }

    [Fact]
    public void ShouldAutoResolve_WhenAtThreshold_ReturnsFalse()
    {
        Assert.False(AlertRules.ShouldAutoResolve(10, 10, existingResolution: null));
    }

    [Fact]
    public void ShouldAutoResolve_WhenOneBelowThreshold_ReturnsTrue()
    {
        Assert.True(AlertRules.ShouldAutoResolve(9, 10, existingResolution: null));
    }

    [Fact]
    public void ShouldAutoResolve_WhenManuallyResolved_ReturnsFalse()
    {
        Assert.False(AlertRules.ShouldAutoResolve(0, 10, ResolutionSource.Manual));
    }

    [Fact]
    public void ShouldAutoResolve_WhenPreviouslyAutoResolved_ReturnsTrue()
    {
        Assert.True(AlertRules.ShouldAutoResolve(0, 10, ResolutionSource.AutoBelowThreshold));
    }
}
