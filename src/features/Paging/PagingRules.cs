using domain.Exceptions;
using FluentValidation;

namespace features.Paging;

/// <summary>
///     The page-number and page-size contract shared by every paged slice.
/// </summary>
/// <remarks>
///     <see cref="ValidPage{T}" /> and <see cref="ValidPageSize{T}" /> are FluentValidation
///     <b>rule-builder extensions</b>, deliberately not an open-generic <c>AbstractValidator&lt;T&gt;</c>
///     base class. <c>AddValidatorsFromAssembly</c> registers closed types only, so an open-generic base
///     would be silently unregistered and every paged endpoint would accept <c>pageSize=100000</c>
///     without a single test failing. Each paged slice writes
///     <c>RuleFor(query =&gt; query.PageSize).ValidPageSize();</c> in its own validator.
///     <para>
///         The constants close O-42's F01a half: 50 and 200 were unsourced numbers in prose and now have
///         a single named home that conventions §2 cites. The submission batch cap of 500 stays
///         unsourced and is F07's to name.
///     </para>
/// </remarks>
public static class PagingRules
{
    public const int DefaultPage = 1;

    public const int DefaultPageSize = 50;

    public const int MaxPageSize = 200;

    /// <summary>
    ///     Resolves <c>?page=</c> to the page number to serve.
    /// </summary>
    /// <remarks>
    ///     The validator tier is the contract and rejects anything below <see cref="DefaultPage" /> with
    ///     a 400. The clamp here is a backstop so a slice that forgets the rule cannot reach
    ///     <c>Skip(negative)</c>, which throws at the provider rather than at the boundary.
    /// </remarks>
    public static int ResolvePage(int? page) => page is null or < DefaultPage ? DefaultPage : page.Value;

    /// <summary>
    ///     Resolves <c>?pageSize=</c> to the page size to apply.
    /// </summary>
    /// <remarks>
    ///     Clamped to <see cref="MaxPageSize" /> for the same reason as <see cref="ResolvePage" />: the
    ///     400 is the contract, but an unclamped size reaching the provider is an unbounded read.
    /// </remarks>
    public static int ResolvePageSize(int? pageSize) => pageSize switch
    {
        null or < 1 => DefaultPageSize,
        > MaxPageSize => MaxPageSize,
        _ => pageSize.Value
    };

    /// <summary>Pages are 1-based. Absent is valid and resolves to <see cref="DefaultPage" />.</summary>
    public static IRuleBuilderOptions<T, int?> ValidPage<T>(this IRuleBuilder<T, int?> rule) =>
        rule.GreaterThanOrEqualTo(DefaultPage)
            .WithErrorCode(ErrorCodes.Validation.Failed)
            .WithMessage(FormattableString.Invariant($"Page must be {DefaultPage} or greater."));

    /// <summary>
    ///     Absent is valid and resolves to <see cref="DefaultPageSize" />; above
    ///     <see cref="MaxPageSize" /> is a 400 <c>VALIDATION.PAGE_SIZE_EXCEEDED</c>.
    /// </summary>
    public static IRuleBuilderOptions<T, int?> ValidPageSize<T>(this IRuleBuilder<T, int?> rule) =>
        rule.GreaterThanOrEqualTo(1)
            .WithErrorCode(ErrorCodes.Validation.Failed)
            .WithMessage("Page size must be 1 or greater.")
            .LessThanOrEqualTo(MaxPageSize)
            .WithErrorCode(ErrorCodes.Validation.PageSizeExceeded)
            .WithMessage(FormattableString.Invariant($"Page size must not exceed {MaxPageSize}."));
}
