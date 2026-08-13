namespace features.Paging;

/// <summary>
///     The <c>page</c> member of conventions §2's collection envelope.
/// </summary>
public sealed record PageInfo
{
    /// <summary>1-based page number actually served, never the raw <c>?page=</c> value.</summary>
    public required int Number { get; init; }

    /// <summary>Page size actually applied, never the raw <c>?pageSize=</c> value.</summary>
    public required int Size { get; init; }

    /// <summary>Total rows matching the query, before <c>Skip</c>/<c>Take</c>.</summary>
    public required int TotalItems { get; init; }

    /// <summary>Number of pages of <see cref="Size" /> rows; 0 when <see cref="TotalItems" /> is 0.</summary>
    public required int TotalPages { get; init; }
}

/// <summary>
///     The envelope every collection endpoint returns. Bare arrays nowhere — conventions §2.
/// </summary>
/// <remarks>
///     Serialises to <c>{ "items": [...], "page": { "number": 1, "size": 50, "totalItems": 412,
///     "totalPages": 9 } }</c>.
///     <para>
///         Offset paging only. F11's submission log needs keyset (<c>?before=</c>) because an
///         append-only log grows without bound, and that is a different envelope with a different
///         <c>page</c> member — deliberately not anticipated here (O-05, O-06).
///     </para>
/// </remarks>
/// <typeparam name="TItem">The projected response type, never an entity.</typeparam>
public sealed record PagedResponse<TItem>
{
    public required IReadOnlyList<TItem> Items { get; init; }

    public required PageInfo Page { get; init; }
}
