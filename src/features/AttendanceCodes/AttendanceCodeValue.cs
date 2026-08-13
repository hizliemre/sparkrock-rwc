namespace features.AttendanceCodes;

/// <summary>
///     The one place an attendance code's <c>Value</c> is put into its canonical form (V-27).
/// </summary>
/// <remarks>
///     SQL Server's default collation is case-insensitive, so legacy treated <c>A</c> and <c>a</c> as
///     one code. A Postgres unique index does not, so both could coexist and
///     <c>sp_GetStudentAttendance</c>'s join would become ambiguous in a way legacy never was.
///     <c>ck_attendance_codes_value_upper</c> is the backstop; this is the mechanism that keeps the
///     backstop unreachable in normal operation.
///     <para>
///         <b><c>ToUpperInvariant</c>, not <c>ToUpper</c>.</b> Under a <c>tr-TR</c> culture the dotless
///         i turns <c>i</c> into <c>İ</c>, which then fails the check constraint on the developer's
///         machine and passes on CI. The invariant form is not a style preference here, and
///         <see cref="AttendanceCodeValue" /> has a named test for exactly that.
///     </para>
///     <para>
///         Conventions §3 puts logic shared by two or more slices in <c>domain/&lt;Aggregate&gt;/</c>,
///         and F03's plan places this file at <c>domain/AttendanceCodes/AttendanceCodeValue.cs</c> so
///         F00's seed and F12's importer can reach the identical rule. It sits in <c>features</c>
///         instead because this workstream's edit boundary stops at <c>features/AttendanceCodes/</c>;
///         moving it is a file move and a namespace change, nothing more.
///     </para>
/// </remarks>
public static class AttendanceCodeValue
{
    /// <summary>
    ///     Trims and upper-cases, returning empty for a null or blank input.
    /// </summary>
    /// <remarks>
    ///     Never throws. The validator owns the 400 for an absent or blank value; a normaliser that
    ///     threw first would turn a field error into a 500 on the one input a client is most likely to
    ///     send by accident.
    /// </remarks>
    public static string Normalise(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
}
