namespace tools.seed;

/// <summary>
///     The seed's copy of F03's attendance-code write-boundary normaliser (V-27).
/// </summary>
/// <remarks>
///     <b>This is a duplicate, and it exists only because of an assembly boundary.</b> F03 ships the
///     canonical rule as <c>features.AttendanceCodes.AttendanceCodeValue.Normalise</c>, and F03's own
///     plan says the file belongs at <c>domain/AttendanceCodes/AttendanceCodeValue.cs</c> precisely so
///     that F00's seed and F12's importer can reach the identical rule — it landed in <c>features</c>
///     only because that workstream's edit boundary stopped there. <c>tools.seed</c> must not
///     reference <c>features</c> (DEC-17: that assembly holds the Carter modules), so until the file
///     moves to <c>domain</c> the seed cannot call it.
///     <para>
///         A copied rule is normally how two spellings of one concept start. That is stopped here by
///         a mechanism rather than a comment: <c>tests/features.tests/Seed/SeedAttendanceCodeValueTests.cs</c>
///         references <em>both</em> assemblies and asserts the two functions agree on a table of
///         inputs, so drift in either direction is a failing test rather than a silent divergence.
///         Delete this type the moment the canonical one reaches <c>domain</c>.
///     </para>
///     <para>
///         <b><c>ToUpperInvariant</c>, not <c>ToUpper</c>.</b> Under <c>tr-TR</c> the dotless i turns
///         <c>i</c> into <c>İ</c>, which then fails <c>ck_attendance_codes_value_upper</c> on the
///         developer's machine and passes on CI.
///     </para>
/// </remarks>
public static class SeedAttendanceCodeValue
{
    /// <summary>Trims and upper-cases, returning empty for a null or blank input.</summary>
    public static string Normalise(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
}
