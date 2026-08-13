using Microsoft.EntityFrameworkCore;

namespace features.Paging;

/// <summary>
///     Turns an ordered, already-projected query into conventions §2's collection envelope.
/// </summary>
public static class PagingExtensions
{
    /// <summary>
    ///     Issues <c>CountAsync</c>, then <c>Skip</c>/<c>Take</c>/<c>ToListAsync</c>.
    /// </summary>
    /// <remarks>
    ///     <b>The caller must apply a total top-level <c>OrderBy</c> first</b> — one ending in a unique
    ///     column, by convention <c>Id</c>. <c>WithPostgre</c> sets
    ///     <c>UseQuerySplittingBehavior(SplitQuery)</c> globally, and a split query whose order is not
    ///     total can return the same row on two pages and omit another (VC-27). There is no way to
    ///     enforce that from here; an unordered <see cref="IQueryable{T}" /> is indistinguishable from an
    ///     ordered one once it is typed as one.
    ///     <para>
    ///         Project before paging. Paging an entity query and projecting afterwards reads whole rows.
    ///     </para>
    /// </remarks>
    public static async Task<PagedResponse<TItem>> ToPagedResponseAsync<TItem>(
        this IQueryable<TItem> source,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        int number = PagingRules.ResolvePage(page);
        int size = PagingRules.ResolvePageSize(pageSize);

        int totalItems = await source.CountAsync(cancellationToken);

        // long, because (number - 1) * size overflows int for a large enough ?page=. Past the end the
        // round trip is skipped entirely rather than issued and discarded.
        long skip = (long)(number - 1) * size;

        List<TItem> items = skip >= totalItems
            ? []
            : await source.Skip((int)skip).Take(size).ToListAsync(cancellationToken);

        return new PagedResponse<TItem>
        {
            Items = items,
            Page = new PageInfo
            {
                Number = number,
                Size = size,
                TotalItems = totalItems,
                TotalPages = (totalItems + size - 1) / size
            }
        };
    }
}
