using api.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace features.tests.Errors;

/// <summary>
///     Where a rejected value came from, inferred from the request rather than assumed.
/// </summary>
/// <remarks>
///     The handler used to write <c>"source": "body"</c> unconditionally. The first paged endpoint
///     binds <c>Page</c> and <c>PageSize</c> from the query string, so the very first violation the
///     scaffold can produce was already mislabelled — and <c>source</c> exists precisely so a
///     malformed route value and a body field of the same name are distinguishable.
/// </remarks>
public sealed class ViolationSourceTests
{
    private static HttpRequest Request(string method, string queryString, params (string Key, string Value)[] routeValues)
    {
        DefaultHttpContext context = new();
        context.Request.Method = method;
        context.Request.QueryString = new QueryString(queryString);

        RouteValueDictionary values = [];

        foreach ((string key, string value) in routeValues)
            values[key] = value;

        context.Request.RouteValues = values;

        return context.Request;
    }

    [Fact]
    public void For_WhenThePropertyMatchesAQueryKey_IsQuery()
    {
        Assert.Equal("query", ViolationSource.For(Request("GET", "?page=0&pageSize=10"), "Page"));
    }

    [Fact]
    public void For_WhenThePropertyMatchesARouteValue_IsPath()
    {
        Assert.Equal(
            "path",
            ViolationSource.For(Request("GET", string.Empty, ("date", "2026-09-14")), "Date"));
    }

    /// <summary>
    ///     A route value wins over a query key of the same name: route values are authoritative
    ///     (conventions §2) and a body must not repeat them.
    /// </summary>
    [Fact]
    public void For_WhenARouteValueAndAQueryKeyShareAName_PrefersPath()
    {
        Assert.Equal(
            "path",
            ViolationSource.For(Request("GET", "?schoolId=x", ("schoolId", "y")), "SchoolId"));
    }

    [Fact]
    public void For_WhenTheRequestHasABodyAndNothingElseMatches_IsBody()
    {
        HttpRequest request = Request("POST", string.Empty);
        request.ContentType = "application/json";
        request.ContentLength = 42;

        Assert.Equal("body", ViolationSource.For(request, "TestProperty"));
    }

    /// <summary>
    ///     A violation cannot have come from a body that was never sent. Reporting <c>body</c> for a
    ///     bodyless GET is the specific lie this replaces.
    /// </summary>
    [Fact]
    public void For_WhenTheRequestHasNoBody_IsNeverBody()
    {
        Assert.NotEqual("body", ViolationSource.For(Request("GET", string.Empty), "PageSize"));
    }

    /// <summary>
    ///     The segment compared is the root of the path, camel-cased the same way the payload key is.
    /// </summary>
    [Theory]
    [InlineData("Entries[3].AttendCode", "?entries=1", "query")]
    [InlineData("IDNumber", "?idNumber=7", "query")]
    public void For_ComparesTheCamelCasedRootSegment(string clrPath, string queryString, string expected)
    {
        Assert.Equal(expected, ViolationSource.For(Request("GET", queryString), clrPath));
    }

    /// <summary>
    ///     A validator can fail on the request object itself, which has no name to match on.
    /// </summary>
    [Fact]
    public void For_WhenThePathIsEmptyAndTheRequestHasABody_IsBody()
    {
        HttpRequest request = Request("POST", string.Empty);
        request.ContentType = "application/json";
        request.ContentLength = 42;

        Assert.Equal("body", ViolationSource.For(request, string.Empty));
    }

    [Fact]
    public void For_WhenTheRequestIsNull_IsBody()
    {
        Assert.Equal("body", ViolationSource.For(null, "TestProperty"));
    }
}
