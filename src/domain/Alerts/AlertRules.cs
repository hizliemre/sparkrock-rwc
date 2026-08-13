using domain.Attendance;

namespace domain.Alerts;

/// <summary>
///     When a chronic-absence alert episode opens and closes (DEC-18).
/// </summary>
/// <remarks>
///     Consumed by both the save pipeline and the alert feature. Legacy raised alerts and never resolved them
///     (L-07); these rules complete that lifecycle without letting it churn or overwrite a human decision.
/// </remarks>
public static class AlertRules
{
    /// <summary>
    ///     Whether a new episode should open.
    /// </summary>
    /// <param name="totalAbsences">Absences for the school year, counted across schools (V-07c).</param>
    /// <param name="schoolThreshold">The student's current school's threshold, or null for the default.</param>
    /// <param name="hasOpenEpisode">
    ///     Whether an unresolved episode already exists for this
    ///     <c>(StudentId, AlertType, SchoolYearStart, SchoolId)</c>. The school is part of the key deliberately —
    ///     keying it school-agnostically let a former school's open alert block the receiving school from raising
    ///     one it could neither see nor resolve, which DEC-16 records as a safeguarding failure.
    /// </param>
    /// <param name="hasManualResolutionThisYear">
    ///     Whether a person has already resolved an episode for this key in this school year.
    /// </param>
    /// <remarks>
    ///     The manual-resolution guard belongs here rather than on the resolve side: resolving closes the episode,
    ///     so the very next save that recounts at or above the threshold would otherwise open a fresh one and
    ///     discard the judgement silently. <paramref name="hasOpenEpisode" /> cannot cover it — a resolved episode
    ///     is not open.
    /// </remarks>
    public static bool ShouldRaise(
        int totalAbsences,
        int? schoolThreshold,
        bool hasOpenEpisode,
        bool hasManualResolutionThisYear)
    {
        return !hasOpenEpisode
               && !hasManualResolutionThisYear
               && AbsenceRules.IsChronicallyAbsent(totalAbsences, schoolThreshold);
    }

    /// <summary>
    ///     Whether an open episode should close automatically.
    /// </summary>
    /// <param name="totalAbsences">Absences for the school year, counted across schools (V-07c).</param>
    /// <param name="schoolThreshold">The student's current school's threshold, or null for the default.</param>
    /// <param name="hasOpenEpisode">Whether there is an unresolved episode to close.</param>
    /// <remarks>
    ///     Resolves strictly below the threshold. The rejected alternative resolved only below
    ///     <c>threshold − 1</c>, which would leave an episode open while the student read as not chronically
    ///     absent, and — because absence counts only rise except under correction — would strand every existing
    ///     alert permanently the moment a school raised its threshold, reinstating L-07 by another route.
    /// </remarks>
    public static bool ShouldAutoResolve(int totalAbsences, int? schoolThreshold, bool hasOpenEpisode)
    {
        return hasOpenEpisode && !AbsenceRules.IsChronicallyAbsent(totalAbsences, schoolThreshold);
    }
}
