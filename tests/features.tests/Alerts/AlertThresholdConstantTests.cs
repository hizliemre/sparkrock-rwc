using System.Text.RegularExpressions;
using domain.Attendance;
using features.tests.Architecture;

namespace features.tests.Alerts;

/// <summary>
///     V-26 and L-10, mechanised for <c>features/Alerts/</c>: the threshold default has exactly one
///     home, and this slice may not grow a second one.
/// </summary>
/// <remarks>
///     <b>Written because mutation testing found the behavioural test could not catch this.</b> The
///     drift predicate cannot call <c>AbsenceRules.ResolveThreshold</c> — a pure static does not
///     translate — so it re-expresses it as <c>?? AbsenceRules.DefaultThreshold</c>. Plan R-5 accepts
///     the duplicated <c>??</c> on the grounds that "the number 10 still appears exactly once in the
///     solution", and names
///     <c>Handle_WhenSchoolThresholdIsNull_UsesTheDomainDefaultForDrift</c> as the mitigation. It is
///     not one: replacing <c>AbsenceRules.DefaultThreshold</c> with the literal <c>10</c> leaves
///     every handler-tier and integration test green, because the two evaluate identically. What the
///     behavioural test catches is a <em>wrong</em> number; only source text catches a
///     <em>duplicated</em> one, which is L-10 exactly — the defect being migrated away from.
///     <para>
///         Scoped to this slice rather than the whole tree because F10 owns only this slice, and a
///         repository-wide version belongs with the other ⚙ checks in
///         <c>tests/features.tests/Architecture/</c>.
///     </para>
/// </remarks>
public sealed class AlertThresholdConstantTests
{
    /// <summary>A coalesce whose fallback is a bare integer literal.</summary>
    private static readonly Regex LiteralCoalesce = new(@"\?\?\s*[0-9]", RegexOptions.None, TimeSpan.FromSeconds(5));

    private static FileInfo[] Sources()
    {
        DirectoryInfo slice = new(Path.Combine(SourceTree.Root().FullName, "src", "features", "Alerts"));

        Assert.True(slice.Exists, $"No slice directory at '{slice.FullName}'; this check would scan nothing.");

        return SourceTree.Sources(slice);
    }

    /// <summary>
    ///     A guard on the guard: a scan over zero files asserts nothing while reporting green, which
    ///     is the defect class this suite exists to prevent.
    /// </summary>
    [Fact]
    public void Sources_AreActuallyBeingScanned() => Assert.NotEmpty(Sources());

    [Fact]
    public void Slice_CoalescesToTheNamedConstantAndNeverToALiteral()
    {
        foreach (FileInfo file in Sources())
        {
            string[] lines = File.ReadAllLines(file.FullName);

            for (int index = 0; index < lines.Length; index++)
            {
                Assert.False(
                    LiteralCoalesce.IsMatch(lines[index]),
                    $"{SourceTree.RelativePath(file)}:{index + 1} coalesces to a numeric literal: "
                    + $"'{lines[index].Trim()}'. The threshold default lives once, in "
                    + $"{nameof(AbsenceRules)}.{nameof(AbsenceRules.DefaultThreshold)} (V-26); a second "
                    + "copy of the number is L-10, the duplicated-business-rule defect this migration "
                    + "exists to remove.");
            }
        }
    }

    /// <summary>
    ///     The positive half. Without it the rule above is satisfied by a slice that stopped
    ///     resolving a default at all.
    /// </summary>
    [Fact]
    public void Slice_ReferencesTheDomainDefault()
    {
        string[] referencing = Sources()
            .Where(file => File.ReadAllText(file.FullName)
                .Contains(
                    $"{nameof(AbsenceRules)}.{nameof(AbsenceRules.DefaultThreshold)}",
                    StringComparison.Ordinal))
            .Select(SourceTree.RelativePath)
            .ToArray();

        Assert.NotEmpty(referencing);
    }
}
