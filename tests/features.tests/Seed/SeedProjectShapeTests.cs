using System.Reflection;
using System.Text.RegularExpressions;
using Carter;
using features.tests.Security;
using tools.seed;

namespace features.tests.Seed;

/// <summary>
///     The shape of <c>tools.seed</c>: what it may contain, what it may reference, and who may
///     reference it.
/// </summary>
/// <remarks>
///     DEC-17 decided that the legacy import is a console tool and not an endpoint, for a reason that
///     applies word for word to the seeder: every <c>ICarterModule</c> in the dependency graph is
///     auto-mounted under the API group by <c>DependencyContextAssemblyCatalog</c>, and
///     <c>Program.cs</c> registers no authentication — so a seed endpoint would be an anonymous bulk
///     write that also, by construction, creates the rows an attacker needs to make other writes
///     succeed. The rule is asserted here rather than restated in prose, because the second console
///     tool is exactly where a rule written for the first one gets forgotten.
/// </remarks>
public sealed class SeedProjectShapeTests
{
    [Fact]
    public void SeedAssembly_ContainsNoCarterModule()
    {
        Type[] types = typeof(SeedCatalog).Assembly.GetTypes();

        // A scan of zero types would pass every assertion below it. That is the failure mode this
        // whole suite exists to prevent.
        Assert.NotEmpty(types);

        string[] modules = types
            .Where(type => typeof(ICarterModule).IsAssignableFrom(type) && !type.IsInterface)
            .Select(type => type.FullName!)
            .ToArray();

        Assert.True(modules.Length == 0,
            $"tools.seed declares {string.Join(", ", modules)} as an ICarterModule. Carter discovers modules "
            + "through the dependency context, so an endpoint here would be mounted anonymously on the API "
            + "(DEC-17).");
    }

    /// <summary>
    ///     <c>features</c> is where the Carter modules live; referencing it is how one arrives in this
    ///     assembly's dependency context without anyone deciding to add an endpoint.
    /// </summary>
    /// <remarks>
    ///     Asserted against the project file rather than against
    ///     <see cref="Assembly.GetReferencedAssemblies" />. The compiler omits a reference whose types
    ///     are never used, so a <c>ProjectReference</c> added today and used tomorrow would be
    ///     invisible to a runtime check on the day it was added — which is the day the review happens.
    /// </remarks>
    [Fact]
    public void SeedProject_DoesNotReferenceFeatures() =>
        Assert.DoesNotContain("features.csproj", ProjectReferences("src/tools.seed/tools.seed.csproj"));

    [Fact]
    public void SeedProject_ReferencesThePersistencePortItWritesThrough() =>
        Assert.Contains("infra.persistence.sql.csproj", ProjectReferences("src/tools.seed/tools.seed.csproj"));

    /// <summary>The seeder must not be reachable from the running API.</summary>
    [Fact]
    public void ApiProject_DoesNotReferenceSeed() =>
        Assert.DoesNotContain("tools.seed.csproj", ProjectReferences("src/api/api.csproj"));

    /// <summary>
    ///     <see cref="SeedGuard.AllowSeedDataKey" /> must never be inheritable from a committed file.
    /// </summary>
    /// <remarks>
    ///     The same rule, and the same scan, as <c>DeploymentGuard</c>'s opt-in. Both flags are the
    ///     real control in their respective guards — the loopback check beside them is defeatable
    ///     (O-16) — and a control that a clone inherits is not a control.
    /// </remarks>
    [Fact]
    public void NoCommittedConfigurationFileContainsTheSeedOptIn()
    {
        string[] sources = RepositoryFiles.ConfigurationSources();

        Assert.NotEmpty(sources);

        foreach (string file in sources)
        {
            Assert.DoesNotContain(
                "AllowSeedData",
                File.ReadAllText(file),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    ///     Reads the <c>ProjectReference</c> file names out of a csproj, asserting the file was found.
    /// </summary>
    private static string[] ProjectReferences(string relativePath)
    {
        FileInfo project = new(Path.Combine(
            RepositoryFiles.Root().FullName,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

        Assert.True(project.Exists,
            $"{project.FullName} does not exist, so this check would scan nothing and pass. If the project "
            + "moved, move the assertion with it.");

        string[] references = Regex
            .Matches(File.ReadAllText(project.FullName), "<ProjectReference\\s[^>]*Include\\s*=\\s*\"([^\"]+)\"")
            .Select(match => Path.GetFileName(match.Groups[1].Value.Replace('\\', '/')))
            .ToArray();

        Assert.NotEmpty(references);

        return references;
    }
}
