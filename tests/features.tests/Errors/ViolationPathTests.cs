using System.Text.Json;
using api.Errors;

namespace features.tests.Errors;

public sealed class ViolationPathTests
{
    /// <summary>
    ///     Per segment, preserving indexers. The framework's camel-case policy lowercases only the
    ///     first character of a whole key and never touches string values at all, so it cannot do
    ///     this — the transform has to run where the violation is built.
    /// </summary>
    [Theory]
    [InlineData("Entries[3].AttendCode", "entries[3].attendCode")]
    [InlineData("TestProperty", "testProperty")]
    [InlineData("Entries[12].Notes", "entries[12].notes")]
    [InlineData("Outer.Inner.Leaf", "outer.inner.leaf")]
    [InlineData("Entries", "entries")]
    [InlineData("", "")]
    public void ToCamelCase_LowersEachSegmentAndKeepsIndexers(string clrPath, string expected)
    {
        Assert.Equal(expected, ViolationPath.ToCamelCase(clrPath));
    }

    [Fact]
    public void ToCamelCase_LeavesAnAlreadyCamelPathUnchanged()
    {
        Assert.Equal("entries[3].attendCode", ViolationPath.ToCamelCase("entries[3].attendCode"));
    }

    /// <summary>
    ///     The path has to name a key that exists in the payload. The serializer writes acronym-leading
    ///     names with the whole leading uppercase run lowered, so a transform that lowers only the first
    ///     character points at <c>iDNumber</c> — a key no client ever sent.
    /// </summary>
    [Theory]
    [InlineData("IDNumber", "idNumber")]
    [InlineData("ID", "id")]
    [InlineData("IOStream", "ioStream")]
    [InlineData("Entries[3].IDNumber", "entries[3].idNumber")]
    [InlineData("Outer.IDNumber.Leaf", "outer.idNumber.leaf")]
    public void ToCamelCase_MatchesTheJsonNamingPolicyForAcronymLeadingNames(string clrPath, string expected)
    {
        Assert.Equal(expected, ViolationPath.ToCamelCase(clrPath));
    }

    /// <summary>
    ///     Every segment must agree with the serializer, not just the ones this test happens to name.
    /// </summary>
    [Theory]
    [InlineData("IDNumber")]
    [InlineData("AttendCode")]
    [InlineData("Notes")]
    [InlineData("HTTPStatus")]
    [InlineData("A")]
    public void ToCamelCase_AgreesWithJsonNamingPolicyCamelCase(string identifier)
    {
        Assert.Equal(JsonNamingPolicy.CamelCase.ConvertName(identifier), ViolationPath.ToCamelCase(identifier));
    }

    /// <summary>
    ///     A null path would serialise as <c>"path": null</c>, which is not a member of the contract.
    /// </summary>
    [Fact]
    public void ToCamelCase_WhenPathIsNull_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, ViolationPath.ToCamelCase(null));
    }
}
