namespace features.tests.Architecture;

/// <summary>
///     Pins the settings that make <c>.editorconfig</c> a build-time mechanism rather than an IDE
///     suggestion (O-38).
/// </summary>
/// <remarks>
///     <c>.editorconfig</c> on its own changes nothing at build time: the <c>IDExxxx</c> rules run
///     only when <c>EnforceCodeStyleInBuild</c> is set, and they are errors only because
///     <c>TreatWarningsAsErrors</c> is. Both live in <c>Directory.Build.props</c>, both are one line,
///     and removing either turns every style rule in the repository into a squiggle nobody outside an
///     IDE sees — with no build output saying so. Twelve parallel workstreams will not all notice.
///     <para>
///         What this test can assert is that the settings are present and say what they must. It
///         cannot assert that the compiler honours them, because that requires compiling. That half
///         was verified by hand instead, and both directions were checked: a file containing
///         <c>var implicitlyTyped = 42;</c> built under these settings failed with
///         <c>error IDE0008</c>, and the same file with <c>-p:EnforceCodeStyleInBuild=false</c> built
///         clean. A block-scoped namespace failed with <c>error IDE0161</c>.
///     </para>
///     <para>
///         That exercise is also where the <c>IDE0007</c> mismatch surfaced. Conventions §3, CLAUDE.md
///         and this file all named <c>IDE0007</c> as the mechanism for "explicit types over var", but
///         IDE0007 is the <i>opposite</i> rule and cannot fire while the style options say
///         <c>false</c>. The rule doing the work is <c>IDE0008</c>, which nothing had named — a ⚙
///         attributed to a rule id that was not enforcing it.
///     </para>
/// </remarks>
public sealed class CodeStyleEnforcementTests
{
    /// <summary>
    ///     <c>EnforceCodeStyleInBuild</c> is what runs the IDE rules during a build;
    ///     <c>TreatWarningsAsErrors</c> is what makes them fail it. Neither is decorative and each is
    ///     independently removable.
    /// </summary>
    [Theory]
    [InlineData("EnforceCodeStyleInBuild", "true")]
    [InlineData("TreatWarningsAsErrors", "true")]
    public void DirectoryBuildProps_SetsTheProperty(string property, string value) =>
        Assert.Matches($@"<{property}>\s*{value}\s*</{property}>", Read("Directory.Build.props"));

    [Theory]
    // The rule ids. IDE0008 rejects `var`; IDE0161 rejects a block-scoped namespace.
    [InlineData(@"dotnet_diagnostic\.IDE0008\.severity\s*=\s*error")]
    [InlineData(@"dotnet_diagnostic\.IDE0161\.severity\s*=\s*error")]
    // The style options the rules read. Without these the ids above have no violation to report:
    // IDE0008 fires only where the style says a type must be explicit.
    [InlineData(@"csharp_style_var_for_built_in_types\s*=\s*false:error")]
    [InlineData(@"csharp_style_var_when_type_is_apparent\s*=\s*false:error")]
    [InlineData(@"csharp_style_var_elsewhere\s*=\s*false:error")]
    [InlineData(@"csharp_style_namespace_declarations\s*=\s*file_scoped:error")]
    public void EditorConfig_PinsTheRule(string pattern) => Assert.Matches(pattern, Read(".editorconfig"));

    /// <summary>
    ///     The severities above are scoped to a <c>[*.cs]</c> section. A rule written above the first
    ///     section header applies to every file type and, for the IDE rules, to none of them usefully.
    /// </summary>
    [Fact]
    public void EditorConfig_ScopesTheRulesToCSharpFiles()
    {
        string content = Read(".editorconfig");

        int section = content.IndexOf("[*.cs]", StringComparison.Ordinal);

        Assert.True(section >= 0, ".editorconfig has no [*.cs] section.");
        Assert.True(content.IndexOf("dotnet_diagnostic.IDE0008.severity", StringComparison.Ordinal) > section,
            "The IDE severities must sit inside the [*.cs] section.");
    }

    /// <summary>
    ///     Guards the file reader itself: a <c>Read</c> that returned an empty string on a wrong path
    ///     would fail every assertion above rather than pass them, but the message would blame the
    ///     wrong thing.
    /// </summary>
    [Theory]
    [InlineData(".editorconfig")]
    [InlineData("Directory.Build.props")]
    public void EnforcementFile_ExistsAtTheRepositoryRoot(string name)
    {
        string path = Path.Combine(SourceTree.Root().FullName, name);

        Assert.True(File.Exists(path), $"{path} does not exist. Every style rule in the repository "
                                       + "depends on it and nothing else reports its absence.");
        Assert.NotEqual(0, new FileInfo(path).Length);
    }

    /// <summary>
    ///     <c>CA1852</c> is deliberately not asserted here: it seals internal types only, so it is not
    ///     the mechanism behind conventions §3's "all concrete types sealed".
    ///     <see cref="SealedTypeTests" /> is.
    /// </summary>
    [Fact]
    public void SealingRule_IsCoveredByAnArchitectureTestRatherThanCA1852()
    {
        Assert.Contains(
            typeof(SealedTypeTests).GetMethods(),
            method => method.Name == nameof(SealedTypeTests.ConcreteTypes_AreSealed));

        Assert.DoesNotMatch(@"dotnet_diagnostic\.CA1852\.severity\s*=\s*none", Read(".editorconfig"));
    }

    private static string Read(string name) => File.ReadAllText(Path.Combine(SourceTree.Root().FullName, name));
}
