using System.Globalization;

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
    ///     The month a school year begins. September, inferred from the legacy inline rule at
    ///     <c>sp_SaveDailyAttendance:32-35</c> — the function it should have called does not exist (D-01, L-13).
    ///     Private deliberately: exposing it would let callers re-inline the rule this type exists to centralise.
    ///     Correcting it is a data migration, not a constant change — every stored value needs re-bucketing (DEC-07).
    /// </summary>
    private const int StartMonth = 9;

    /// <summary>
    ///     Earliest supported start year.
    /// </summary>
    public const int MinStartYear = 1900;

    /// <summary>
    ///     Latest supported start year.
    /// </summary>
    public const int MaxStartYear = 2100;

    /// <summary>
    ///     The calendar year this school year starts in.
    /// </summary>
    /// <remarks>
    ///     A record struct cannot suppress its parameterless constructor, so <c>default(SchoolYear)</c> carries
    ///     zero here and no factory can prevent it being written. Every factory rejects out-of-range values, and
    ///     the database carries the same bound as a check constraint.
    /// </remarks>
    public int StartYear { get; private init; }

    /// <summary>
    ///     Constructs from a start year, rejecting anything outside the supported range.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The year is outside the supported range.</exception>
    public static SchoolYear FromStartYear(int startYear)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(startYear, MinStartYear);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(startYear, MaxStartYear);

        return new SchoolYear { StartYear = startYear };
    }

    /// <summary>
    ///     Resolves the school year containing a school-local date.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The resulting year is outside the supported range.</exception>
    public static SchoolYear FromLocalDate(DateOnly schoolLocalDate)
    {
        int startYear = schoolLocalDate.Month >= StartMonth
            ? schoolLocalDate.Year
            : schoolLocalDate.Year - 1;

        return FromStartYear(startYear);
    }

    /// <summary>
    ///     Parses the canonical "YYYY-YYYY" form.
    /// </summary>
    /// <remarks>
    ///     Deliberately strict. The default integer parse accepts leading signs, surrounding whitespace and
    ///     leading zeros under the current culture, which would give one school year many accepted spellings —
    ///     none of which round-trip through <see cref="ToString" />. Only the exact rendered form is accepted.
    /// </remarks>
    public static bool TryParse(string? value, out SchoolYear schoolYear)
    {
        schoolYear = default;

        if (string.IsNullOrEmpty(value))
            return false;

        string[] parts = value.Split('-');

        if (parts.Length != 2 || parts[0].Length != 4 || parts[1].Length != 4)
            return false;

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int startYear)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int endYear))
            return false;

        if (startYear < MinStartYear || startYear > MaxStartYear || endYear != startYear + 1)
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
    ///     <para>
    ///         Guarded because <c>default(SchoolYear)</c> is reachable however careful the factories are: a record
    ///         struct always has a parameterless constructor, and <see cref="TryParse" /> writes <c>default</c> to
    ///         its out parameter on every failure path, so a caller that ignores the return value holds one. The
    ///         guard turns an opaque <see cref="DateOnly" /> range error into something diagnosable.
    ///     </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The value is <c>default</c> or otherwise out of range.</exception>
    public (DateOnly From, DateOnly ToExclusive) ToDateRange()
    {
        if (StartYear < MinStartYear || StartYear > MaxStartYear)
        {
            throw new InvalidOperationException(
                $"SchoolYear carries an unusable start year ({StartYear}). Construct it through "
                + $"{nameof(FromLocalDate)} or {nameof(FromStartYear)}; default(SchoolYear) is not a school year.");
        }

        return (new DateOnly(StartYear, StartMonth, 1), new DateOnly(StartYear + 1, StartMonth, 1));
    }

    public override string ToString()
    {
        return string.Create(CultureInfo.InvariantCulture, $"{StartYear}-{StartYear + 1}");
    }
}
