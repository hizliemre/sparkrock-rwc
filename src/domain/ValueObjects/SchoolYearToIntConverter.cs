using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace domain.ValueObjects;

/// <summary>
///     Persists <see cref="SchoolYear" /> as its start year.
/// </summary>
/// <remarks>
///     An integer rather than the legacy VARCHAR(9): it orders and ranges naturally, needs no length or format
///     constraint, and if the September boundary ever proves wrong the correction is arithmetic on one column
///     rather than a rewrite of every stored string and the index built over it.
///     <para>
///         Only whole-value comparisons translate. Filtering by a range of years goes through
///         <see cref="SchoolYear.ToDateRange" /> against the date column, never against this one.
///     </para>
/// </remarks>
public sealed class SchoolYearToIntConverter : ValueConverter<SchoolYear, int>
{
    public SchoolYearToIntConverter()
        : base(
            schoolYear => schoolYear.StartYear,
            startYear => SchoolYear.FromStartYear(startYear))
    {
    }
}
