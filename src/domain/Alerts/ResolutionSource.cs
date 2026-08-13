namespace domain.Alerts;

/// <summary>
///     What closed an alert episode.
/// </summary>
/// <remarks>
///     Kept distinct so a human decision is never mistaken for, or silently undone by, an automatic one.
/// </remarks>
public enum ResolutionSource
{
    /// <summary>
    ///     Closed by a person.
    /// </summary>
    Manual = 1,

    /// <summary>
    ///     Closed automatically when a correction dropped the absence count below the threshold.
    /// </summary>
    AutoBelowThreshold = 2
}
