using System.Collections.Generic;
using System.Globalization;

namespace tools.seed;

/// <summary>
///     The fixed primary keys every seeded row carries.
/// </summary>
/// <remarks>
///     Literal rather than generated, and that is what makes the seed an <em>upsert by primary
///     key</em>: re-running matches on the key instead of guessing a natural one, so a second run
///     produces no second copy. F01c settled that primary keys are client-generated with no
///     <c>gen_random_uuid()</c> default, so assigning <c>Id</c> is the ordinary path here rather than
///     a workaround.
///     <para>
///         Every id begins <c>f0000000-0000-4000-8000-</c>. The prefix is not decoration: it is what
///         makes the O-30 cutover precondition checkable with one query —
///         <c>SELECT count(*) FROM attendance_codes WHERE id::text LIKE 'f0%'</c> must return 0
///         before an import, because the cutover database is import-only and is never seeded. An id
///         that escaped the prefix would make that check silently under-report, so
///         <c>SeedIds_AllUseTheReservedPrefix</c> pins it.
///     </para>
///     <para>
///         The last twelve hex digits are <c>KKNN</c> — kind then ordinal — so a row's identity is
///         readable in a screenshot or a bug report and reproducible on another developer's machine.
///     </para>
/// </remarks>
public static class SeedIds
{
    /// <summary>The reserved prefix. Nothing outside F00 may issue an id that starts with this.</summary>
    public const string ReservedPrefix = "f0000000-0000-4000-8000-";

    /// <summary>Kind <c>00</c>, ordinal <c>01</c>. One school, per design §5.</summary>
    public static readonly Guid School = Make(0, 1);

    public static readonly IReadOnlyList<Guid> AttendanceCodes = Range(kind: 1, count: 5);

    public static readonly IReadOnlyList<Guid> SchoolTerms = Range(kind: 2, count: 4);

    public static readonly IReadOnlyList<Guid> Students = Range(kind: 3, count: 32);

    /// <summary>Every seeded id, in declaration order. The uniqueness and prefix tests read this.</summary>
    public static IReadOnlyList<Guid> All() =>
    [
        School,
        .. AttendanceCodes,
        .. SchoolTerms,
        .. Students
    ];

    private static Guid[] Range(int kind, int count)
    {
        Guid[] ids = new Guid[count];

        for (int ordinal = 1; ordinal <= count; ordinal++)
            ids[ordinal - 1] = Make(kind, ordinal);

        return ids;
    }

    /// <summary>
    ///     Builds <c>f0000000-0000-4000-8000-00000000<em>KK</em><em>NN</em></c> — the final group of a
    ///     Guid is twelve hex digits, of which eight are padding, two are the kind and two the ordinal.
    /// </summary>
    /// <remarks>
    ///     Formatted with <see cref="CultureInfo.InvariantCulture" />. A culture-sensitive integer
    ///     format would be an odd way to break a primary key, but the digits are being pasted into a
    ///     string that must parse as a Guid on every machine, and CA1305 is an error here anyway.
    /// </remarks>
    private static Guid Make(int kind, int ordinal) =>
        Guid.Parse(string.Create(
            CultureInfo.InvariantCulture,
            $"{ReservedPrefix}00000000{kind:D2}{ordinal:D2}"));
}
