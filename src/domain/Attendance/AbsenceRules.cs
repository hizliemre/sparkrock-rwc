namespace domain.Attendance;

/// <summary>
///     The absence threshold rules, in one place.
/// </summary>
/// <remarks>
///     Legacy defaulted a null school threshold to 10 in two separate procedures, and expressed the chronic test
///     twice. Both now live here.
/// </remarks>
public static class AbsenceRules
{
    /// <summary>
    ///     Applied when a school has not configured its own threshold.
    /// </summary>
    public const int DefaultThreshold = 10;

    /// <summary>
    ///     The threshold governing a school, falling back to <see cref="DefaultThreshold" />.
    /// </summary>
    public static int ResolveThreshold(int? schoolThreshold)
    {
        return schoolThreshold ?? DefaultThreshold;
    }

    /// <summary>
    ///     Whether an absence total meets its school's threshold.
    /// </summary>
    /// <remarks>
    ///     A raw count, not a percentage of enrolled days. That is the legacy definition and is preserved
    ///     deliberately — the conventional meaning of "chronic absenteeism" is the percentage one, so this
    ///     divergence from the common reading is intentional rather than an oversight.
    /// </remarks>
    public static bool IsChronicallyAbsent(int totalAbsences, int? schoolThreshold)
    {
        return totalAbsences >= ResolveThreshold(schoolThreshold);
    }
}
