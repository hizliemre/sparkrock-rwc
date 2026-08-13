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
}
