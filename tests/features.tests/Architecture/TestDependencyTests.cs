using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace features.tests.Architecture;

/// <summary>
///     Asserts no mocking or fluent-assertion package has entered the build (conventions §6).
/// </summary>
/// <remarks>
///     The rule carried a ⚙ and had no check of any kind (O-38). It is worth a mechanism because the
///     first such package arrives in a single line of a single csproj, in a branch whose diff is about
///     something else entirely, and because the cost it imposes — a second assertion vocabulary, or a
///     handler tested against a mock of <c>IDbContext</c> instead of the real model — is paid by
///     everyone afterwards.
///     <para>
///         Four independent sources are checked, because each misses something the others catch:
///         project files catch the direct addition, <c>Directory.Packages.props</c> catches a version
///         pinned ahead of the reference, the resolved dependency graph catches a package arriving
///         transitively under some other package's name, and the loaded assemblies catch an assembly
///         that reached the output directory by any route at all, including a committed binary.
///     </para>
///     <para>
///         Every source asserts that it actually read something — a scan of zero project files, or of
///         a dependency manifest that is not where it was expected, would otherwise report green
///         while checking nothing. That is the failure mode this suite exists to prevent, and a test
///         that greps a path that might be wrong is the purest form of it.
///     </para>
/// </remarks>
public sealed class TestDependencyTests
{
    /// <summary>
    ///     Matched as a whole package id or as the first dotted segment of one, so <c>Moq.AutoMock</c>
    ///     is caught and a hypothetical <c>Moqui.Client</c> is not.
    /// </summary>
    private static readonly string[] Banned =
    [
        "Moq",
        "NSubstitute",
        "FakeItEasy",
        "Rhino.Mocks",
        "JustMock",
        "Telerik.JustMock",
        "NMock",
        "NMock3",
        "Machine.Fakes",
        "AutoFixture",
        "FluentAssertions",
        "Shouldly"
    ];

    [Fact]
    public void ProjectFiles_ReferenceNoMockingPackage()
    {
        (string Project, string Package)[] references = ProjectReferences();

        Assert.True(references.Length > 0, "No PackageReference found in any csproj — the scan read nothing.");
        Assert.Contains(references, reference => reference.Package == "xunit");

        string[] violations = references
            .Where(reference => IsBanned(reference.Package))
            .Select(reference => $"{reference.Project} references {reference.Package}")
            .ToArray();

        Assert.True(violations.Length == 0, Explain(violations));
    }

    [Fact]
    public void ProjectFiles_AreAllBeingScanned()
    {
        FileInfo[] projects = ProjectFiles();

        Assert.True(projects.Length >= 8,
            $"Only {projects.Length} project files found under {SourceTree.Root().FullName}. "
            + "The solution has more than that, so the scan is not seeing the repository.");
    }

    [Fact]
    public void CentralPackageVersions_PinNoMockingPackage()
    {
        FileInfo manifest = new(Path.Combine(SourceTree.Root().FullName, "Directory.Packages.props"));

        Assert.True(manifest.Exists, $"{manifest.FullName} does not exist; central package management "
                                     + "has moved and this check is reading nothing.");

        string[] pinned = PackageIds(File.ReadAllText(manifest.FullName), "PackageVersion");

        Assert.Contains("xunit", pinned, StringComparer.OrdinalIgnoreCase);

        string[] violations = pinned
            .Where(IsBanned)
            .Select(package => $"Directory.Packages.props pins {package}")
            .ToArray();

        Assert.True(violations.Length == 0, Explain(violations));
    }

    /// <summary>
    ///     The resolved graph, transitive dependencies included. A mocking library pulled in under
    ///     another package's name appears here and nowhere else.
    /// </summary>
    [Fact]
    public void ResolvedDependencies_ContainNoMockingPackage()
    {
        string[] resolved = ResolvedLibraries();

        Assert.Contains(resolved, library => library.Equals("xunit", StringComparison.OrdinalIgnoreCase));

        string[] violations = resolved
            .Where(IsBanned)
            .Select(library => $"the resolved dependency graph contains {library}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.True(violations.Length == 0, Explain(violations));
    }

    [Fact]
    public void LoadedAssemblies_ContainNoMockingLibrary()
    {
        string[] loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetName().Name!)
            .ToArray();

        Assert.Contains(loaded, name => name.StartsWith("xunit", StringComparison.OrdinalIgnoreCase));

        string[] violations = loaded
            .Where(IsBanned)
            .Select(name => $"{name} is loaded into the test host")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.True(violations.Length == 0, Explain(violations));
    }

    /// <summary>
    ///     Pins the matcher itself. Every assertion above is a filter over a list; a matcher that
    ///     matched nothing would make all of them pass on any input.
    /// </summary>
    [Theory]
    [InlineData("Moq", true)]
    [InlineData("moq", true)]
    [InlineData("Moq.AutoMock", true)]
    [InlineData("NSubstitute.Analyzers.CSharp", true)]
    [InlineData("FakeItEasy", true)]
    [InlineData("FluentAssertions", true)]
    [InlineData("Telerik.JustMock", true)]
    [InlineData("xunit", false)]
    [InlineData("Microsoft.Extensions.TimeProvider.Testing", false)]
    [InlineData("Moqui.Client", false)]
    [InlineData("Microsoft.EntityFrameworkCore.InMemory", false)]
    public void IsBanned_ClassifiesPackageIds(string package, bool expected) =>
        Assert.Equal(expected, IsBanned(package));

    private static bool IsBanned(string package) =>
        Banned.Any(banned => package.Equals(banned, StringComparison.OrdinalIgnoreCase)
                             || package.StartsWith($"{banned}.", StringComparison.OrdinalIgnoreCase));

    private static FileInfo[] ProjectFiles() =>
        SourceTree.Root()
            .GetFiles("*.csproj", SearchOption.AllDirectories)
            .Where(file => !file.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .ToArray();

    private static (string Project, string Package)[] ProjectReferences() =>
        ProjectFiles()
            .SelectMany(file => PackageIds(File.ReadAllText(file.FullName), "PackageReference")
                .Select(package => (file.Name, package)))
            .ToArray();

    private static string[] PackageIds(string projectXml, string element) =>
        Regex.Matches(projectXml, "<" + element + "\\s[^>]*Include\\s*=\\s*\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .ToArray();

    /// <summary>
    ///     Reads the dependency manifest the test host itself was launched against, next to the
    ///     running assembly — not a path assembled from a guess about the output layout.
    /// </summary>
    private static string[] ResolvedLibraries()
    {
        string name = typeof(TestDependencyTests).Assembly.GetName().Name!;
        FileInfo manifest = new(Path.Combine(AppContext.BaseDirectory, $"{name}.deps.json"));

        Assert.True(manifest.Exists,
            $"No dependency manifest at {manifest.FullName}. The transitive half of this check would "
            + "otherwise silently inspect an empty list.");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifest.FullName));

        Assert.True(document.RootElement.TryGetProperty("libraries", out JsonElement libraries),
            $"{manifest.Name} has no 'libraries' member; the manifest format has changed.");

        return libraries.EnumerateObject().Select(library => library.Name.Split('/')[0]).ToArray();
    }

    private static string Explain(string[] violations) =>
        string.Join("; ", violations)
        + ". Conventions §6 allows xUnit Assert only — no mocking package and no fluent-assertion "
        + "package. Handlers are tested against the real DbContext from InMemoryDbContextFactory and "
        + "test doubles are internal sealed classes in tests/features.tests/Fakes/.";
}
