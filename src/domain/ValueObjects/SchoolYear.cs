namespace domain.ValueObjects;

/// <summary>
///     A school year, identified by the calendar year it starts in. Rendered as "2026-2027".
/// </summary>
/// <remarks>
///     The legacy system wrote this rule three times and called a fourth version that does not exist, so the
///     boundary lived in no single place. Every derivation now goes through here.
///     <para>
///         Dates passed in are school-local. This type never reads a clock — the caller resolves "today" in the
///         school's timezone and passes a <see cref="DateOnly" />.
///     </para>
/// </remarks>
public readonly record struct SchoolYear
{
    /// <summary>
    ///     The month a school year begins. September, inferred from the legacy inline rule.
    /// </summary>
    public const int StartMonth = 9;

    /// <summary>
    ///     The calendar year this school year starts in.
    /// </summary>
    public int StartYear { get; init; }

    /// <summary>
    ///     Resolves the school year containing a school-local date.
    /// </summary>
    public static SchoolYear FromLocalDate(DateOnly schoolLocalDate)
    {
        int startYear = schoolLocalDate.Month >= StartMonth
            ? schoolLocalDate.Year
            : schoolLocalDate.Year - 1;

        return new SchoolYear { StartYear = startYear };
    }

    /// <summary>
    ///     Parses the "YYYY-YYYY" form. Rejects non-consecutive years and any non-positive start year, so the
    ///     rendering of <c>default(SchoolYear)</c> cannot round-trip.
    /// </summary>
    public static bool TryParse(string? value, out SchoolYear schoolYear)
    {
        schoolYear = default;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        string[] parts = value.Split('-');

        if (parts.Length != 2)
            return false;

        if (!int.TryParse(parts[0], out int startYear) || !int.TryParse(parts[1], out int endYear))
            return false;

        if (startYear <= 0 || endYear != startYear + 1)
            return false;

        schoolYear = new SchoolYear { StartYear = startYear };
        return true;
    }

    /// <summary>
    ///     The half-open date range this school year spans.
    /// </summary>
    /// <remarks>
    ///     Filtering attendance by this range is what keeps the query sargable. Comparing a computed school year
    ///     against a stored value — as the legacy read did — defeats the index on the date column.
    /// </remarks>
    public (DateOnly From, DateOnly ToExclusive) ToDateRange()
    {
        return (new DateOnly(StartYear, StartMonth, 1), new DateOnly(StartYear + 1, StartMonth, 1));
    }

    public override string ToString()
    {
        return $"{StartYear}-{StartYear + 1}";
    }
}
