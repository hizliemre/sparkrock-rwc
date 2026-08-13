using domain.Attendance;

namespace domain.Alerts;

/// <summary>
///     When a chronic-absence alert episode opens and closes.
/// </summary>
/// <remarks>
///     Consumed by both the save pipeline and the alert feature. Legacy raised alerts and never resolved them;
///     these rules complete that lifecycle without letting it churn or overwrite a human decision.
/// </remarks>
public static class AlertRules
{
    /// <summary>
    ///     Whether a new episode should open.
    /// </summary>
    public static bool ShouldRaise(int totalAbsences, int? schoolThreshold, bool hasOpenEpisode)
    {
        return !hasOpenEpisode && AbsenceRules.IsChronicallyAbsent(totalAbsences, schoolThreshold);
    }

    /// <summary>
    ///     Whether an open episode should close automatically.
    /// </summary>
    /// <remarks>
    ///     Resolves strictly below the threshold, with no hysteresis band. Resolving one below instead would leave
    ///     an episode open while the student reads as not chronically absent, and — because absence counts only
    ///     rise except under correction — would strand every existing alert permanently the moment a school raised
    ///     its threshold.
    ///     <para>
    ///         An episode a person resolved is never reopened automatically.
    ///     </para>
    /// </remarks>
    public static bool ShouldAutoResolve(int totalAbsences, int? schoolThreshold, ResolutionSource? existingResolution)
    {
        return existingResolution != ResolutionSource.Manual
               && !AbsenceRules.IsChronicallyAbsent(totalAbsences, schoolThreshold);
    }
}
