using System.Collections.Generic;
using System.Linq;

namespace tools.seed;

/// <summary>What the writer did to one kind of row.</summary>
public sealed record SeedOutcome
{
    public required string Entity { get; init; }

    public required int Created { get; init; }

    public required int Updated { get; init; }

    public required int Unchanged { get; init; }

    /// <summary>
    ///     Rows the writer refused to touch because writing them would have overwritten something it
    ///     does not own. See <see cref="SeedResult.Conflicts" />.
    /// </summary>
    public required int Skipped { get; init; }
}

/// <summary>
///     The summary a run prints.
/// </summary>
/// <remarks>
///     A tool that prints nothing gives a developer no way to know whether it did anything, and
///     "created 0, updated 0" is the observable form of idempotency — the property this feature is
///     most likely to lose and least likely to notice losing.
/// </remarks>
public sealed record SeedResult
{
    public required IReadOnlyList<SeedOutcome> Outcomes { get; init; }

    /// <summary>
    ///     Human-readable descriptions of rows that were deliberately left alone.
    /// </summary>
    /// <remarks>
    ///     Non-empty means the database already held a row the seed would otherwise have collided
    ///     with — in practice an <c>AttendanceCode</c> whose <c>Value</c> is one of the seeded five
    ///     but whose <c>Id</c> is not, created through F03's API or adopted by F12. The seed neither
    ///     overwrites it nor inserts beside it, because <c>ix_attendance_codes_value</c> is unique and
    ///     <b>unfiltered</b>: inserting would be a <c>23505</c> that aborts the whole run, and
    ///     overwriting would silently rewrite a row somebody else owns.
    ///     <para>
    ///         <c>Program</c> exits non-zero when this is non-empty. A skipped row that is only
    ///         mentioned in a summary nobody reads is the silent failure this project keeps producing.
    ///     </para>
    /// </remarks>
    public required IReadOnlyList<string> Conflicts { get; init; }

    public int TotalCreated => Outcomes.Sum(outcome => outcome.Created);

    public int TotalUpdated => Outcomes.Sum(outcome => outcome.Updated);

    public int TotalUnchanged => Outcomes.Sum(outcome => outcome.Unchanged);

    public int TotalSkipped => Outcomes.Sum(outcome => outcome.Skipped);
}
