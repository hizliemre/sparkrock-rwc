using System.Globalization;
using System.Text.RegularExpressions;
using domain.AttendanceCodes;
using domain.SchoolTerms;
using domain.Students;
using domain.ValueObjects;
using tools.seed;

namespace features.tests.Seed;

/// <summary>
///     The seed's <b>content</b>, asserted with no provider, no host and no clock.
/// </summary>
/// <remarks>
///     <see cref="SeedCatalog.Build" /> is a pure function from a <see cref="SchoolYear" /> to a
///     <see cref="SeedPlan" />, which is what makes every rule about the data — non-overlapping
///     terms, uppercase code values, null legacy ids, the deliberate term gaps, the reserved id
///     prefix — assertable here rather than against a database.
/// </remarks>
public sealed class SeedCatalogTests
{
    private static readonly SchoolYear Year = SchoolYear.FromStartYear(2026);

    private static SeedPlan Plan() => SeedCatalog.Build(Year);

    // ---- Identity scheme -------------------------------------------------------------------

    [Fact]
    public void SeedIds_AreAllUnique()
    {
        IReadOnlyList<Guid> all = SeedIds.All();

        Assert.Equal(1 + 5 + 4 + 32, all.Count);
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    /// <summary>
    ///     Every seeded id starts <c>f0000000-0000-4000-8000-</c>.
    /// </summary>
    /// <remarks>
    ///     This is what makes the O-30 cutover precondition query — <c>WHERE id::text LIKE 'f0%'</c>
    ///     — mean anything. An id that escaped the prefix would make the check silently under-report,
    ///     and the report it under-reports is the one that says "this database has never been
    ///     seeded, it is safe to import into".
    /// </remarks>
    [Fact]
    public void SeedIds_AllUseTheReservedPrefix()
    {
        foreach (Guid id in SeedIds.All())
        {
            Assert.StartsWith(
                SeedIds.ReservedPrefix,
                id.ToString("D", CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
        }
    }

    /// <summary>Every id in the plan comes from <see cref="SeedIds" /> — nothing is generated.</summary>
    [Fact]
    public void Build_AssignsEveryRowAnIdFromSeedIds()
    {
        SeedPlan plan = Plan();

        Guid[] used =
        [
            plan.School.Id,
            .. plan.AttendanceCodes.Select(code => code.Id),
            .. plan.Terms.Select(term => term.Id),
            .. plan.Students.Select(student => student.Id)
        ];

        Assert.Equal(SeedIds.All().Order().ToArray(), used.Order().ToArray());
    }

    /// <summary>
    ///     No row is adoptable by a legacy row before F12 decides it is (O-30).
    /// </summary>
    /// <remarks>
    ///     Seeded rows are not legacy rows. Populating <c>LegacyId</c> here would make them adoptable
    ///     by the <em>wrong</em> legacy rows, which is worse than not being adoptable at all.
    /// </remarks>
    [Fact]
    public void Build_LeavesEveryLegacyIdNull()
    {
        SeedPlan plan = Plan();

        Assert.Null(plan.School.LegacyId);
        Assert.All(plan.AttendanceCodes, code => Assert.Null(code.LegacyId));
        Assert.All(plan.Terms, term => Assert.Null(term.LegacyId));
        Assert.All(plan.Students, student => Assert.Null(student.LegacyId));
    }

    // ---- Attendance codes ------------------------------------------------------------------

    [Fact]
    public void Build_ProducesFiveAttendanceCodes() => Assert.Equal(5, Plan().AttendanceCodes.Count);

    /// <summary>
    ///     V-27's write-boundary half. The check constraint is the backstop, this is the mechanism.
    /// </summary>
    [Fact]
    public void Build_AttendanceCodeValuesAreUppercase()
    {
        foreach (AttendanceCode code in Plan().AttendanceCodes)
        {
            Assert.Equal(code.Value.ToUpperInvariant(), code.Value);
            Assert.Equal(code.Value.Trim(), code.Value);
            Assert.NotEqual(string.Empty, code.Value);
        }
    }

    [Fact]
    public void Build_AttendanceCodeValuesAreDistinct()
    {
        string[] values = Plan().AttendanceCodes.Select(code => code.Value).ToArray();

        Assert.Equal(values.Length, values.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    ///     <c>X</c> exists so F03's <c>?includeInactive</c>, F07's inactive-code rejection (V-14) and
    ///     DEC-19's "history renders deactivated codes" all have data to run against.
    /// </summary>
    [Fact]
    public void Build_IncludesOneInactiveAttendanceCode()
    {
        AttendanceCode[] inactive = Plan().AttendanceCodes.Where(code => !code.IsActive).ToArray();

        AttendanceCode retired = Assert.Single(inactive);
        Assert.Equal("X", retired.Value);
    }

    /// <summary>
    ///     "Excused" qualifies an absence and means nothing without one.
    /// </summary>
    /// <remarks>
    ///     F01c ships no check constraint for the pairing and none is proposed: F01d's snapshot
    ///     columns carry whatever was recorded, and inventing the constraint now would reject legacy
    ///     rows on import. So the rule lives here, over the data F00 controls.
    /// </remarks>
    [Fact]
    public void Build_ExcusedIsFalseWhereverAbsentIsFalse()
    {
        foreach (AttendanceCode code in Plan().AttendanceCodes.Where(code => !code.IsAbsent))
            Assert.False(code.IsExcused, $"Code '{code.Value}' is not an absence but is marked excused.");
    }

    [Fact]
    public void Build_IncludesAnAbsenceCodeOfEachExcusedKind()
    {
        AttendanceCode[] codes = [.. Plan().AttendanceCodes];

        Assert.Contains(codes, code => code.IsAbsent && !code.IsExcused && code.IsActive);
        Assert.Contains(codes, code => code.IsAbsent && code.IsExcused && code.IsActive);
    }

    // ---- School ----------------------------------------------------------------------------

    /// <summary>
    ///     F01c declined to validate <c>TimeZoneId</c> and named F02 as the owner.
    /// </summary>
    /// <remarks>
    ///     Until that lands, a seed with an unresolvable zone would make F07 throw
    ///     <see cref="TimeZoneNotFoundException" /> at write time for the only school that exists —
    ///     and the seeder itself resolves the same id to compute school-local today, so it would not
    ///     even finish.
    /// </remarks>
    [Fact]
    public void Build_SchoolTimeZoneIsResolvable()
    {
        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(Plan().School.TimeZoneId);

        // Negative offset, so UtcNow.Date and school-local today differ for part of every day and
        // DEC-12's failure mode is reproducible by hand rather than theoretical.
        Assert.True(zone.BaseUtcOffset < TimeSpan.Zero);
    }

    /// <summary>Null, so the read paths exercise <c>AbsenceRules.ResolveThreshold(null) == 10</c> (V-26).</summary>
    [Fact]
    public void Build_SchoolThresholdIsNull() => Assert.Null(Plan().School.AbsenceAlertThreshold);

    [Fact]
    public void Build_SchoolIsActive() => Assert.True(Plan().School.IsActive);

    [Fact]
    public void Build_SchoolNameIsObviouslySynthetic() =>
        Assert.Contains("Demo", Plan().School.Name, StringComparison.Ordinal);

    // ---- Terms -----------------------------------------------------------------------------

    [Fact]
    public void Build_ProducesFourTerms() => Assert.Equal(4, Plan().Terms.Count);

    /// <summary>
    ///     The seed must not be able to violate V-19 before F04 exists to reject it.
    /// </summary>
    /// <remarks>
    ///     Pairwise, under <b>closed</b> bounds: <c>a.Start &lt;= b.End &amp;&amp; b.Start &lt;= a.End</c>
    ///     must be false for every distinct pair of active terms. Reading <c>EndDate</c> as exclusive
    ///     would shift every term by a day and quietly permit an adjacent pair to touch.
    /// </remarks>
    [Theory]
    [InlineData(2024)]
    [InlineData(2026)]
    [InlineData(2027)]
    public void Build_ActiveTermsDoNotOverlap(int startYear)
    {
        SchoolTerm[] active = SeedCatalog.Build(SchoolYear.FromStartYear(startYear))
            .Terms.Where(term => term.IsActive)
            .ToArray();

        Assert.Equal(3, active.Length);

        for (int left = 0; left < active.Length; left++)
        {
            for (int right = left + 1; right < active.Length; right++)
            {
                Assert.False(
                    Overlaps(active[left], active[right]),
                    $"'{active[left].Name}' and '{active[right].Name}' overlap under closed bounds.");
            }
        }
    }

    /// <summary>
    ///     The inverse assertion, deliberately.
    /// </summary>
    /// <remarks>
    ///     "Fall (superseded)" overlaps Term 1 and is inactive, which is what makes F04's reactivation
    ///     rule reproducible by hand: <c>PUT {isActive: true}</c> on it must return 409
    ///     <c>TERM.OVERLAP</c>. That is the easiest interaction in F04 to implement and forget, and
    ///     this row is what makes forgetting visible.
    /// </remarks>
    [Fact]
    public void Build_SupersededTermOverlapsTermOneAndIsInactive()
    {
        IReadOnlyList<SchoolTerm> terms = Plan().Terms;

        SchoolTerm superseded = terms.Single(term => !term.IsActive);
        SchoolTerm first = terms.Single(term => term.Name == "Term 1");

        Assert.True(Overlaps(superseded, first));
    }

    /// <summary>
    ///     D-03's preserved "no term matches, <c>TermId</c> stays null" path needs a date to occur on.
    /// </summary>
    /// <remarks>
    ///     A seed with continuous coverage would make that path unreachable by hand, so the gaps are
    ///     content rather than an accident of the dates chosen. Named dates rather than "at least one
    ///     gap exists": a single-day gap would satisfy the weaker form while being useless to run a
    ///     submission against.
    /// </remarks>
    [Theory]
    [InlineData(2026, 12, 21)]
    [InlineData(2027, 1, 5)]
    [InlineData(2027, 3, 14)]
    [InlineData(2027, 3, 22)]
    [InlineData(2027, 6, 27)]
    [InlineData(2027, 8, 31)]
    public void Build_LeavesTheseDatesCoveredByNoActiveTerm(int year, int month, int day) =>
        Assert.False(CoveredByAnActiveTerm(new DateOnly(year, month, day)));

    [Theory]
    [InlineData(2026, 9, 1)]
    [InlineData(2026, 12, 20)]
    [InlineData(2027, 1, 6)]
    [InlineData(2027, 3, 13)]
    [InlineData(2027, 3, 23)]
    [InlineData(2027, 6, 26)]
    public void Build_CoversTheseDatesWithAnActiveTerm(int year, int month, int day) =>
        Assert.True(CoveredByAnActiveTerm(new DateOnly(year, month, day)));

    /// <summary>
    ///     Term bounds are closed; the school-year range is half-open at the end. The two are easy to
    ///     conflate, and conflating them puts a term one day outside the year it belongs to.
    /// </summary>
    [Theory]
    [InlineData(2024)]
    [InlineData(2026)]
    [InlineData(2027)]
    public void Build_TermsFallWithinTheGivenSchoolYear(int startYear)
    {
        SchoolYear schoolYear = SchoolYear.FromStartYear(startYear);
        (DateOnly from, DateOnly toExclusive) = schoolYear.ToDateRange();

        foreach (SchoolTerm term in SeedCatalog.Build(schoolYear).Terms)
        {
            Assert.True(term.StartDate >= from, $"'{term.Name}' starts before the school year.");
            Assert.True(term.EndDate < toExclusive, $"'{term.Name}' ends on or after the year's exclusive end.");
            Assert.True(term.EndDate >= term.StartDate, $"'{term.Name}' ends before it starts.");
        }
    }

    [Fact]
    public void Build_AllTermsBelongToTheSeededSchool() =>
        Assert.All(Plan().Terms, term => Assert.Equal(SeedIds.School, term.SchoolId));

    // ---- Students --------------------------------------------------------------------------

    [Fact]
    public void Build_ProducesThirtyTwoStudents() => Assert.Equal(32, Plan().Students.Count);

    [Fact]
    public void Build_AllStudentsBelongToTheSeededSchool() =>
        Assert.All(Plan().Students, student => Assert.Equal(SeedIds.School, student.SchoolId));

    /// <summary>
    ///     Four grades over the first thirty, then two nulls.
    /// </summary>
    /// <remarks>
    ///     The nullable grade is L-15's runtime error 94 and the case an optional <c>?grade=</c>
    ///     filter must handle explicitly (V-24, D-06) rather than silently include or exclude.
    /// </remarks>
    [Fact]
    public void Build_StudentGradesCoverFourValuesAndTwoNulls()
    {
        IReadOnlyList<Student> students = Plan().Students;

        Assert.Equal(2, students.Count(student => student.Grade is null));
        Assert.Equal(
            ["09", "10", "11", "12"],
            students.Where(student => student.Grade is not null)
                .Select(student => student.Grade!)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray());

        // Non-trivial: a grade filter that returned one row would prove nothing about paging or
        // ordering.
        foreach (string grade in (string[])["09", "10", "11", "12"])
            Assert.True(students.Count(student => student.Grade == grade) >= 7);
    }

    /// <summary>
    ///     For F05's <c>?includeInactive</c>, and for the save pipeline's deliberate <em>non</em>-check
    ///     on inactive students (legacy-analysis §4, preserved behaviours).
    /// </summary>
    [Fact]
    public void Build_IncludesTwoInactiveStudents() =>
        Assert.Equal(2, Plan().Students.Count(student => !student.IsActive));

    /// <summary>
    ///     A weak guard, deliberately kept: it makes a realistic-looking name a failing test rather
    ///     than a review comment. Seed data has a way of ending up in demos and screenshots.
    /// </summary>
    [Fact]
    public void Build_StudentNamesAreSynthetic()
    {
        foreach (Student student in Plan().Students)
        {
            Assert.Equal("Demo", student.FirstName);
            Assert.Matches(new Regex(@"^Student\d{2}$", RegexOptions.None, TimeSpan.FromSeconds(1)),
                student.LastName);
        }
    }

    [Fact]
    public void Build_StudentNamesAreDistinct()
    {
        string[] names = Plan().Students.Select(student => student.LastName).ToArray();

        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }

    // ---- Purity ----------------------------------------------------------------------------

    /// <summary>
    ///     Same year in, same rows out; a different year changes dates and nothing else.
    /// </summary>
    /// <remarks>
    ///     If ids moved with the year, a database seeded in June and re-seeded in October would end
    ///     up with two sets of terms — overlapping ones, which is the state V-19 exists to prevent.
    /// </remarks>
    [Fact]
    public void Build_IsDeterministicAndDependsOnTheYearOnlyForDates()
    {
        SeedPlan first = SeedCatalog.Build(SchoolYear.FromStartYear(2026));
        SeedPlan later = SeedCatalog.Build(SchoolYear.FromStartYear(2030));

        Assert.Equal(
            first.Terms.Select(term => term.Id).ToArray(),
            later.Terms.Select(term => term.Id).ToArray());

        Assert.NotEqual(
            first.Terms.Select(term => term.StartDate).ToArray(),
            later.Terms.Select(term => term.StartDate).ToArray());

        Assert.Equal(first.School.Name, later.School.Name);
    }

    // ---- Helpers ---------------------------------------------------------------------------

    /// <summary>Closed bounds on both sides, matching F01c §3 and D-03.</summary>
    private static bool Overlaps(SchoolTerm left, SchoolTerm right) =>
        left.StartDate <= right.EndDate && right.StartDate <= left.EndDate;

    private static bool CoveredByAnActiveTerm(DateOnly date) =>
        Plan().Terms.Any(term => term.IsActive && term.StartDate <= date && date <= term.EndDate);
}
