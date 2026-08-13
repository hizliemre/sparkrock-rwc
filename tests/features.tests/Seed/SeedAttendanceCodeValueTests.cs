using domain.AttendanceCodes;
using features.AttendanceCodes;
using tools.seed;

namespace features.tests.Seed;

/// <summary>
///     Pins the seed's copy of the attendance-code normaliser to F03's original.
/// </summary>
/// <remarks>
///     F03 owns the canonical rule (V-27) and its own plan places the file at
///     <c>domain/AttendanceCodes/</c> precisely so F00's seed and F12's importer can call it; it
///     landed in <c>features</c> only because that workstream's edit boundary stopped there.
///     <c>tools.seed</c> must not reference <c>features</c> (DEC-17), so the seed carries a copy —
///     and a copied rule is normally how two spellings of one concept begin.
///     <para>
///         <b>This test is the mechanism that stops that.</b> <c>features.tests</c> is the only
///         assembly that can see both functions, so it is the only place the equivalence can be
///         asserted at all. If either implementation changes, this fails; when the canonical rule
///         moves to <c>domain</c>, this file and <see cref="SeedAttendanceCodeValue" /> are both
///         deleted.
///     </para>
/// </remarks>
public sealed class SeedAttendanceCodeValueTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("p")]
    [InlineData("P")]
    [InlineData("  e  ")]
    [InlineData("Ab")]
    [InlineData("aB")]
    // The Turkish dotless i: ToUpper() under tr-TR yields 'İ', which fails
    // ck_attendance_codes_value_upper on the developer's machine and passes on CI.
    [InlineData("i")]
    [InlineData("ı")]
    [InlineData("straße")]
    public void Normalise_AgreesWithTheCanonicalRule(string? value) =>
        Assert.Equal(AttendanceCodeValue.Normalise(value), SeedAttendanceCodeValue.Normalise(value));

    /// <summary>
    ///     The seeded values are normalised, not merely written in upper case by hand.
    /// </summary>
    /// <remarks>
    ///     Asserted against F03's function rather than against <c>ToUpperInvariant</c>, so the seed is
    ///     tied to the rule the API enforces on the same column rather than to a coincidence.
    /// </remarks>
    [Fact]
    public void EverySeededValue_IsAlreadyItsNormalisedForm()
    {
        foreach (AttendanceCode code in SeedCatalog.Build(domain.ValueObjects.SchoolYear.FromStartYear(2026))
                     .AttendanceCodes)
        {
            Assert.Equal(AttendanceCodeValue.Normalise(code.Value), code.Value);
        }
    }
}
