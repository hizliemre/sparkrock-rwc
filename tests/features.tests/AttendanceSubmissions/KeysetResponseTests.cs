using System.Text.Json;
using features.Paging;

namespace features.tests.AttendanceSubmissions;

/// <summary>
///     O-05's resolution made mechanical: the keyset envelope is a <b>second</b> envelope, and its
///     <c>page</c> object carries no total.
/// </summary>
/// <remarks>
///     Conventions §2 mandates one collection envelope with
///     <c>number/size/totalItems/totalPages</c> and, four lines later, says F11 uses keyset. Those two
///     sentences contradict each other, and every member of the mandated <c>page</c> object is either
///     meaningless under keyset (<c>number</c> — a cursor names a position, not an ordinal) or costs a
///     <c>COUNT(*)</c> on every page (<c>totalItems</c>, and <c>totalPages</c> derived from it), which
///     is the one cost keyset exists to avoid.
///     <para>
///         So the risk this file exists to cover is <b>not</b> a bug. It is someone later "restoring
///         consistency" with the offset envelope by adding <c>totalItems</c>, reintroducing the
///         per-page count with no test failing. <see cref="Serialise_PageHasExactlyTheKeysetMembers" />
///         is what stops them, and it asserts on the <em>serialised member names</em> rather than on
///         the CLR properties: a <c>nameof</c>-based assertion renames along with the property it
///         guards and so can never fire.
///     </para>
/// </remarks>
public sealed class KeysetResponseTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private static readonly string[] OffsetOnlyMembers = ["number", "totalItems", "totalPages"];

    [Fact]
    public void Serialise_ProducesItemsAndPage()
    {
        JsonElement root = Serialise(hasMore: true);

        // The two envelopes agree at the top level, so a client's generic "unwrap the envelope" code
        // works on both. What differs is what is inside `page`, which is what has to differ.
        Assert.Equal(["items", "page"], MemberNames(root));
        Assert.Equal(JsonValueKind.Array, root.GetProperty("items").ValueKind);
        Assert.Equal(JsonValueKind.Object, root.GetProperty("page").ValueKind);
    }

    /// <summary>
    ///     Conventions §2: absent optional fields are omitted, not <c>null</c>. A client's paging loop
    ///     terminates on the field's absence, which is unambiguous; <c>null</c> and <c>""</c> are both
    ///     easy to treat as truthy by accident.
    /// </summary>
    [Fact]
    public void Serialise_WhenHasMoreIsFalse_OmitsNextCursor()
    {
        JsonElement page = Serialise(hasMore: false).GetProperty("page");

        Assert.False(page.TryGetProperty("nextCursor", out JsonElement _));
        Assert.Equal(["hasMore", "size"], MemberNames(page).Order().ToArray());
    }

    [Fact]
    public void Serialise_WhenHasMoreIsTrue_CarriesNextCursor()
    {
        JsonElement page = Serialise(hasMore: true).GetProperty("page");

        Assert.Equal("cursor-value", page.GetProperty("nextCursor").GetString());
    }

    /// <summary>
    ///     The member set, both directions: exactly what keyset can honestly compute, and none of what
    ///     the offset envelope carries.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Serialise_PageHasExactlyTheKeysetMembers(bool hasMore)
    {
        string[] members = MemberNames(Serialise(hasMore).GetProperty("page")).Order().ToArray();

        Assert.Equal(hasMore ? ["hasMore", "nextCursor", "size"] : ["hasMore", "size"], members);

        foreach (string offsetOnly in OffsetOnlyMembers)
            Assert.DoesNotContain(offsetOnly, members, StringComparer.Ordinal);
    }

    /// <summary>
    ///     A guard on the guard above: the offset envelope really does carry the three members the
    ///     keyset one is asserted not to have. Without this, a rename in <c>PageInfo</c> would leave
    ///     <see cref="OffsetOnlyMembers" /> naming nothing and the assertion vacuous.
    /// </summary>
    [Fact]
    public void Serialise_TheOffsetEnvelopeStillCarriesTheMembersKeysetOmits()
    {
        JsonElement page = JsonSerializer.SerializeToElement(
                new PagedResponse<string>
                {
                    Items = ["a"],
                    Page = new PageInfo { Number = 1, Size = 50, TotalItems = 1, TotalPages = 1 }
                },
                WebOptions)
            .GetProperty("page");

        foreach (string offsetOnly in OffsetOnlyMembers)
            Assert.Contains(offsetOnly, MemberNames(page), StringComparer.Ordinal);
    }

    [Fact]
    public void Serialise_WhenNoItems_ProducesAnEmptyArrayNotNull()
    {
        JsonElement items = JsonSerializer.SerializeToElement(
                new KeysetResponse<string>
                {
                    Items = [],
                    Page = new KeysetPageInfo { Size = 50, HasMore = false }
                },
                WebOptions)
            .GetProperty("items");

        Assert.Equal(JsonValueKind.Array, items.ValueKind);
        Assert.Empty(items.EnumerateArray());
    }

    private static JsonElement Serialise(bool hasMore) => JsonSerializer.SerializeToElement(
        new KeysetResponse<string>
        {
            Items = ["first", "second"],
            Page = new KeysetPageInfo
            {
                Size = 50,
                HasMore = hasMore,
                NextCursor = hasMore ? "cursor-value" : null
            }
        },
        WebOptions);

    private static string[] MemberNames(JsonElement element) =>
        element.EnumerateObject().Select(property => property.Name).ToArray();
}
