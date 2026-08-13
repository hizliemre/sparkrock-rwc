using System.Collections.Generic;
using System.Globalization;
using domain.AttendanceCodes;
using domain.Schools;
using domain.SchoolTerms;
using domain.Students;
using domain.ValueObjects;

namespace tools.seed;

/// <summary>
///     The seed's content. Pure: no I/O, no clock, no database.
/// </summary>
/// <remarks>
///     Purity is what makes the content testable. Everything interesting about this data is a rule
///     about the data itself — the active terms must not overlap (V-19), the code values must be
///     uppercase (V-27), every <c>LegacyId</c> must be null (O-30), the school year must have gaps
///     where no term matches (D-03) — and every one of those is asserted against the returned
///     <see cref="SeedPlan" /> with no provider involved.
///     <para>
///         The clock is read by <c>Program</c>, converted into <see cref="SchoolTimeZoneId" />, and
///         passed in as a <see cref="SchoolYear" />. This type never reads one, for the same reason
///         <see cref="SchoolYear" /> does not: <c>DateTimeOffset.UtcNow</c> and <c>DateTime.Now</c>
///         are banned in <c>features</c> and <c>domain</c>, and a tool is not the exception that
///         proves the rule.
///     </para>
/// </remarks>
public static class SeedCatalog
{
    /// <summary>
    ///     The seeded school's IANA zone.
    /// </summary>
    /// <remarks>
    ///     A real id in a negative-offset zone, chosen so that <c>UtcNow.Date</c> and school-local
    ///     today differ for part of every day. DEC-12's failure mode is then reproducible by hand
    ///     rather than theoretical. Declared as a constant because <c>Program</c> needs it to resolve
    ///     school-local today <em>before</em> a plan exists to read it from.
    /// </remarks>
    public const string SchoolTimeZoneId = "America/Toronto";

    /// <summary>Obviously synthetic. See <c>Build_StudentNamesAreSynthetic</c> for the student half.</summary>
    public const string SchoolName = "Rideau Demo School";

    /// <summary>The first name every seeded student carries.</summary>
    public const string StudentFirstName = "Demo";

    private const int StudentCount = 32;

    /// <summary>Students beyond this ordinal carry a null <c>Grade</c>.</summary>
    private const int GradedStudentCount = 30;

    private static readonly string[] GradeCycle = ["09", "10", "11", "12"];

    /// <summary>Ordinals (1-based) of the students seeded inactive.</summary>
    private static readonly int[] InactiveStudentOrdinals = [29, 30];

    public static SeedPlan Build(SchoolYear schoolYear) =>
        new()
        {
            School = BuildSchool(),
            AttendanceCodes = BuildAttendanceCodes(),
            Terms = BuildTerms(schoolYear),
            Students = BuildStudents()
        };

    /// <summary>
    ///     One school.
    /// </summary>
    /// <remarks>
    ///     <c>AbsenceAlertThreshold</c> is null deliberately, so the read paths exercise
    ///     <c>AbsenceRules.ResolveThreshold(null) == 10</c> (V-26) rather than a hard-coded number.
    ///     <para>
    ///         A second school would make cross-tenant behaviour demonstrable by hand, but the
    ///         running application registers a system-administrator stub with an empty scope, so
    ///         nothing in it would distinguish them. Tenancy is exercised by <c>FakeCurrentUser</c> at
    ///         the handler tier instead.
    ///     </para>
    /// </remarks>
    private static School BuildSchool() =>
        new()
        {
            Id = SeedIds.School,
            Name = SchoolName,
            TimeZoneId = SchoolTimeZoneId,
            AbsenceAlertThreshold = null,
            IsActive = true,
            LegacyId = null
        };

    /// <summary>
    ///     Five codes, covering the legacy vocabulary plus one retired code.
    /// </summary>
    /// <remarks>
    ///     <c>Value</c> goes through <see cref="SeedAttendanceCodeValue.Normalise" /> rather than
    ///     being trusted to be uppercase in the literal above it. The check constraint
    ///     <c>ck_attendance_codes_value_upper</c> is the backstop; normalising is the mechanism, and
    ///     the difference matters because a lowercase literal that reached the database would then
    ///     collide case-sensitively with F12's import (V-27).
    ///     <para>
    ///         <c>IsExcused</c> is false wherever <c>IsAbsent</c> is false — "excused" qualifies an
    ///         absence and means nothing without one. F01c ships no check constraint for that pairing
    ///         and none is proposed: F01d's snapshot columns carry whatever was recorded, and
    ///         inventing the constraint now would reject legacy rows on import.
    ///     </para>
    ///     <para>
    ///         <c>X</c> is inactive so that three downstream behaviours have data: F03's
    ///         <c>?includeInactive</c>, F07's rejection of an inactive code (V-14), and DEC-19's
    ///         requirement that F08 render history whose code has since been deactivated.
    ///     </para>
    ///     <para>
    ///         Every <c>LegacyId</c> is null. That is the precondition for F12's adoption branch
    ///         (O-30): a seeded row must be adoptable by the legacy row carrying the same
    ///         <c>UPPER(Value)</c>, and populating <c>LegacyId</c> here would make it adoptable by the
    ///         <em>wrong</em> one.
    ///     </para>
    /// </remarks>
    private static IReadOnlyList<AttendanceCode> BuildAttendanceCodes() =>
    [
        Code(0, "P", "Present", isAbsent: false, isExcused: false, isActive: true),
        Code(1, "A", "Absent — unexcused", isAbsent: true, isExcused: false, isActive: true),
        Code(2, "E", "Absent — excused", isAbsent: true, isExcused: true, isActive: true),
        Code(3, "L", "Late", isAbsent: false, isExcused: false, isActive: true),
        Code(4, "X", "Retired code", isAbsent: true, isExcused: false, isActive: false)
    ];

    private static AttendanceCode Code(
        int index,
        string value,
        string description,
        bool isAbsent,
        bool isExcused,
        bool isActive) =>
        new()
        {
            Id = SeedIds.AttendanceCodes[index],
            Value = SeedAttendanceCodeValue.Normalise(value),
            Description = description,
            IsAbsent = isAbsent,
            IsExcused = isExcused,
            IsActive = isActive,
            LegacyId = null
        };

    /// <summary>
    ///     Four terms, dated inside the school year passed in.
    /// </summary>
    /// <remarks>
    ///     Term bounds are <b>closed</b> — <c>[StartDate, EndDate]</c>, F01c §3 and D-03 — while
    ///     <see cref="SchoolYear.ToDateRange" /> is half-open at the end. The two are easy to
    ///     conflate and <c>Build_TermsFallWithinTheGivenSchoolYear</c> pins the distinction.
    ///     <para>
    ///         The three active terms are pairwise non-overlapping, so the seed cannot itself violate
    ///         V-19. The <b>gaps are deliberate</b>: Dec 21–Jan 5, Mar 14–22 and Jun 27–Aug 31 are
    ///         covered by no active term, which is D-03's preserved "no term matches, <c>TermId</c>
    ///         stays null" path. A seed with continuous coverage would make that path unreachable by
    ///         hand.
    ///     </para>
    ///     <para>
    ///         "Fall (superseded)" overlaps Term 1 <em>and is inactive</em>, which is what makes F04's
    ///         reactivation rule reproducible: <c>PUT {isActive: true}</c> on it must return 409
    ///         <c>TERM.OVERLAP</c>. That is the easiest interaction in F04 to implement and forget,
    ///         and this row is what makes forgetting visible.
    ///     </para>
    ///     <para>
    ///         Dates are recomputed for the then-current school year on every run and written over the
    ///         same four ids. A seed whose terms silently expired at the end of August would make F07
    ///         reject every submission with no obvious cause.
    ///     </para>
    /// </remarks>
    private static IReadOnlyList<SchoolTerm> BuildTerms(SchoolYear schoolYear)
    {
        int start = schoolYear.StartYear;
        int end = start + 1;

        return
        [
            Term(0, "Term 1", new DateOnly(start, 9, 1), new DateOnly(start, 12, 20), isActive: true),
            Term(1, "Term 2", new DateOnly(end, 1, 6), new DateOnly(end, 3, 13), isActive: true),
            Term(2, "Term 3", new DateOnly(end, 3, 23), new DateOnly(end, 6, 26), isActive: true),
            Term(3, "Fall (superseded)", new DateOnly(start, 9, 1), new DateOnly(start, 10, 31), isActive: false)
        ];
    }

    private static SchoolTerm Term(int index, string name, DateOnly startDate, DateOnly endDate, bool isActive) =>
        new()
        {
            Id = SeedIds.SchoolTerms[index],
            SchoolId = SeedIds.School,
            Name = name,
            StartDate = startDate,
            EndDate = endDate,
            IsActive = isActive,
            LegacyId = null
        };

    /// <summary>
    ///     Thirty-two students, all in the seeded school.
    /// </summary>
    /// <remarks>
    ///     32 is above F07's per-request comfort and below the 50 default page size, so a roster fits
    ///     on one page while paging stays exercisable with <c>?pageSize=10</c>.
    ///     <para>
    ///         Grades cycle <c>09</c>–<c>12</c> over the first thirty, so <c>?grade=09</c> returns a
    ///         non-trivial subset (F05, and F06's V-24 filter). Students 31 and 32 have a
    ///         <b>null</b> grade — the nullable column behind L-15's runtime error 94, and the case a
    ///         grade filter must not silently include or exclude by accident.
    ///     </para>
    ///     <para>
    ///         Students 29 and 30 are inactive, for F05's <c>?includeInactive</c> and for the save
    ///         pipeline's deliberate <em>non</em>-check on inactive students (legacy-analysis §4).
    ///     </para>
    ///     <para>
    ///         Names are synthetic by construction — there is no generator and no realistic-looking
    ///         data in this repository. The <c>Demo</c>/<c>StudentNN</c> scheme is chosen so that a
    ///         real-looking name added later is visibly out of place, and
    ///         <c>Build_StudentNamesAreSynthetic</c> turns it into a failing test rather than a review
    ///         comment.
    ///     </para>
    /// </remarks>
    private static Student[] BuildStudents()
    {
        Student[] students = new Student[StudentCount];

        for (int ordinal = 1; ordinal <= StudentCount; ordinal++)
        {
            students[ordinal - 1] = new Student
            {
                Id = SeedIds.Students[ordinal - 1],
                SchoolId = SeedIds.School,
                FirstName = StudentFirstName,
                LastName = "Student" + ordinal.ToString("D2", CultureInfo.InvariantCulture),
                Grade = ordinal <= GradedStudentCount ? GradeCycle[(ordinal - 1) % GradeCycle.Length] : null,
                IsActive = !Array.Exists(InactiveStudentOrdinals, inactive => inactive == ordinal),
                LegacyId = null
            };
        }

        return students;
    }
}
