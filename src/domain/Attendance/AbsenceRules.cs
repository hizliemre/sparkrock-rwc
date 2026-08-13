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
    /// <remarks>
    ///     A configured threshold of zero or below would make every student chronically absent and raise an alert
    ///     for the whole roster, so it is rejected rather than honoured. Nothing in the schema prevents such a
    ///     value being stored, which is why the guard is here as well as on the column.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The configured threshold is not positive.</exception>
    public static int ResolveThreshold(int? schoolThreshold)
    {
        if (schoolThreshold is null)
            return DefaultThreshold;

        ArgumentOutOfRangeException.ThrowIfLessThan(schoolThreshold.Value, 1);

        return schoolThreshold.Value;
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
