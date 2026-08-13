using System.Linq.Expressions;
using domain.SchoolTerms;

namespace features.tests.SchoolTerms;

/// <summary>
///     The boundary matrix for V-19's predicate, at the unit tier.
/// </summary>
/// <remarks>
///     Pure date arithmetic over compiled expressions — no provider, no context. Conventions §6's tier
///     rule is why the handler tests do not repeat the matrix: they assert the probe is wired in, not
///     that its arithmetic is right.
///     <para>
///         <b>Bounds are closed.</b> Every other range in the system is half-open; this is F01c §3's
///         one deliberate exception, because legacy resolved a term with <c>BETWEEN</c>. A <c>&lt;</c>
///         where the predicate needs <c>&lt;=</c> loses the last day of every term, and reads as a
///         data-entry mistake for months. These are the tests that catch it.
///     </para>
/// </remarks>
public sealed class TermOverlapTests
{
    private static readonly Guid SchoolId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static readonly Guid OtherSchoolId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static readonly DateOnly ExistingStart = new(2026, 9, 1);

    private static readonly DateOnly ExistingEnd = new(2026, 12, 20);

    /// <summary>
    ///     Closed bounds. Half-open would make this false, and the last day of every term would be
    ///     silently shareable.
    /// </summary>
    [Fact]
    public void Overlapping_WhenCandidateStartsOnTheExistingEndDate_IsTrue() =>
        Assert.True(Matches(Existing(), new DateOnly(2026, 12, 20), new DateOnly(2026, 12, 31)));

    [Fact]
    public void Overlapping_WhenCandidateStartsTheDayAfter_IsFalse() =>
        Assert.False(Matches(Existing(), new DateOnly(2026, 12, 21), new DateOnly(2026, 12, 31)));

    [Fact]
    public void Overlapping_WhenCandidateEndsOnTheExistingStartDate_IsTrue() =>
        Assert.True(Matches(Existing(), new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 1)));

    [Fact]
    public void Overlapping_WhenCandidateEndsTheDayBefore_IsFalse() =>
        Assert.False(Matches(Existing(), new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31)));

    [Fact]
    public void Overlapping_WhenCandidateIsFullyContained_IsTrue() =>
        Assert.True(Matches(Existing(), new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)));

    [Fact]
    public void Overlapping_WhenCandidateFullyContainsTheExisting_IsTrue() =>
        Assert.True(Matches(Existing(), new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1)));

    [Fact]
    public void Overlapping_WhenCandidateIsIdentical_IsTrue() =>
        Assert.True(Matches(Existing(), ExistingStart, ExistingEnd));

    /// <summary>Deactivation is how a term is superseded, so an inactive term blocks nothing.</summary>
    [Fact]
    public void Overlapping_WhenExistingIsInactive_IsFalse() =>
        Assert.False(Matches(Existing(isActive: false), ExistingStart, ExistingEnd));

    [Fact]
    public void Overlapping_WhenExistingBelongsToAnotherSchool_IsFalse() =>
        Assert.False(Matches(Existing(schoolId: OtherSchoolId), ExistingStart, ExistingEnd));

    /// <summary>A term does not conflict with itself on update.</summary>
    [Fact]
    public void Overlapping_WhenExistingIsTheExcludedTerm_IsFalse()
    {
        SchoolTerm existing = Existing();

        Assert.False(Matches(existing, ExistingStart, ExistingEnd, excludingTermId: existing.Id));
    }

    /// <summary>
    ///     The create path has no term to exclude and passes <see cref="Guid.Empty" />, which is never
    ///     a real key. A nullable would emit <c>@p IS NULL OR id &lt;&gt; @p</c> for no benefit.
    /// </summary>
    [Fact]
    public void Overlapping_WhenExcludingGuidEmpty_ExcludesNothing() =>
        Assert.True(Matches(Existing(), ExistingStart, ExistingEnd, excludingTermId: Guid.Empty));

    /// <summary>
    ///     The predicate has to translate to SQL, so it is an <see cref="Expression{TDelegate}" /> and
    ///     not a <c>bool</c> method. A static predicate called inside <c>Where</c> fails at translation
    ///     time rather than at compile time, which is a run-time 500 on the write path.
    /// </summary>
    [Fact]
    public void Overlapping_ReturnsAnExpressionRatherThanADelegate()
    {
        Expression<Func<SchoolTerm, bool>> predicate =
            TermOverlap.Overlapping(SchoolId, ExistingStart, ExistingEnd, Guid.Empty);

        Assert.Equal(ExpressionType.Lambda, predicate.NodeType);
    }

    private static SchoolTerm Existing(Guid? schoolId = null, bool isActive = true) => new()
    {
        Id = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
        SchoolId = schoolId ?? SchoolId,
        Name = "Term 1",
        StartDate = ExistingStart,
        EndDate = ExistingEnd,
        IsActive = isActive
    };

    private static bool Matches(
        SchoolTerm existing,
        DateOnly candidateStart,
        DateOnly candidateEnd,
        Guid? excludingTermId = null) =>
        TermOverlap
            .Overlapping(SchoolId, candidateStart, candidateEnd, excludingTermId ?? Guid.NewGuid())
            .Compile()
            .Invoke(existing);
}
