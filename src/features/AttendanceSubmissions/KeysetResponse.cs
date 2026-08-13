using System.Text.Json.Serialization;

namespace features.Paging;

/// <summary>
///     The <c>page</c> member of the <b>keyset</b> collection envelope (O-05).
/// </summary>
/// <remarks>
///     Deliberately not <see cref="PageInfo" />'s member set, and the difference is the finding rather
///     than an omission:
///     <list type="bullet">
///         <item><c>number</c> — there is no page number. A cursor names a position, not an ordinal.</item>
///         <item>
///             <c>totalItems</c> — needs a <c>COUNT(*)</c> over the filtered log <em>on every page</em>,
///             which is the exact cost keyset exists to avoid.
///         </item>
///         <item><c>totalPages</c> — derived from <c>totalItems</c>; inherits both its cost and its meaninglessness.</item>
///     </list>
///     A client that needs a total for an append-only log needs a different endpoint, not a slower
///     one. <c>KeysetResponseTests.Serialise_PageHasExactlyTheKeysetMembers</c> pins the member set
///     against a later "restoration of consistency" with the offset envelope.
/// </remarks>
public sealed record KeysetPageInfo
{
    /// <summary>Page size actually applied, never the raw <c>?pageSize=</c> value.</summary>
    public required int Size { get; init; }

    /// <summary>
    ///     Whether another page exists.
    /// </summary>
    /// <remarks>
    ///     A bool, not a count. Computed by fetching <c>size + 1</c> rows and trimming — no
    ///     <c>COUNT(*)</c>, one round trip.
    /// </remarks>
    public required bool HasMore { get; init; }

    /// <summary>
    ///     The cursor that starts the next page; <b>omitted</b> when <see cref="HasMore" /> is false.
    /// </summary>
    /// <remarks>
    ///     Omitted rather than <c>null</c> (conventions §2). A client's paging loop terminates on the
    ///     field's absence, which is unambiguous; <c>null</c> and <c>""</c> are both easy to treat as
    ///     truthy by accident. Nothing in the kernel configures <c>WhenWritingNull</c> globally, so the
    ///     attribute is per-property, matching how F02 handles <c>absenceAlertThreshold</c>.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextCursor { get; init; }
}

/// <summary>
///     The envelope a keyset-paged collection endpoint returns — the second of conventions §2's two
///     envelopes, for append-only collections that grow without bound.
/// </summary>
/// <remarks>
///     Serialises to
///     <c>{ "items": [...], "page": { "size": 50, "hasMore": true, "nextCursor": "…" } }</c>.
///     <para>
///         <b><c>items</c> and <c>page</c> are the two top-level members on purpose</b>, so the two
///         envelopes agree at the JSON-pointer level and a client's generic "unwrap the envelope" code
///         works on both. What differs is what is inside <c>page</c>, which is what has to differ —
///         see <see cref="KeysetPageInfo" />.
///     </para>
///     <para>
///         There is deliberately <b>no</b> common base or interface shared with
///         <see cref="PagedResponse{TItem}" />. They have <c>items</c> in common and nothing else; a
///         shared base would have to declare a <c>page</c> of some common type, and the whole of O-05
///         is that the two <c>page</c> objects share only <c>size</c>.
///     </para>
///     <para>
///         <b>Where this file lives.</b> The F11 plan puts it in <c>src/features/Paging/</c> next to
///         <see cref="PagedResponse{TItem}" />, whose own remarks point here. It sits under
///         <c>AttendanceSubmissions/</c> because the F11 implementation task's edit boundary excluded
///         <c>src/features/Paging/</c>; the namespace is <c>features.Paging</c> either way, so moving
///         the file changes no code.
///     </para>
/// </remarks>
/// <typeparam name="TItem">The projected response type, never an entity.</typeparam>
public sealed record KeysetResponse<TItem>
{
    public required IReadOnlyList<TItem> Items { get; init; }

    public required KeysetPageInfo Page { get; init; }
}
