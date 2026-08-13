using System.Reflection;
using System.Text.RegularExpressions;

namespace features.tests.Architecture;

/// <summary>
///     Asserts every banned symbol actually resolves to something real.
/// </summary>
/// <remarks>
///     <c>BannedApiAnalyzers</c> silently ignores an entry it cannot bind: no error, no warning, no
///     diagnostic of any kind. A near-miss signature therefore produces a ban that never fires,
///     inside a file that reads as though it does — and the mistake is invisible until someone writes
///     the code the ban was supposed to stop.
///     <para>
///         That happened here. Four entries across two projects were inert while the documentation,
///         the commit message and a passing probe of a fifth entry all said enforcement was in place.
///         This test exists so the next near-miss fails immediately instead of years later.
///     </para>
/// </remarks>
public sealed class BannedSymbolsTests
{
    public static TheoryData<string, string> AllEntries()
    {
        TheoryData<string, string> data = [];

        foreach (FileInfo file in RepositoryRoot().GetFiles("BannedSymbols.txt", SearchOption.AllDirectories))
        {
            foreach (string line in File.ReadAllLines(file.FullName))
            {
                string trimmed = line.Trim();

                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                    continue;

                data.Add(Path.GetFileName(file.DirectoryName)!, trimmed.Split(';')[0]);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllEntries))]
    public void BannedSymbol_ResolvesToARealMember(string project, string docId)
    {
        Assert.True(Resolve(docId), $"{project}/BannedSymbols.txt declares '{docId}', which resolves to nothing. "
                                    + "The analyzer ignores unresolvable entries silently, so this ban never fires.");
    }

    /// <summary>
    ///     A <c>M:</c> entry written with no parameter list must name a genuinely parameterless
    ///     overload, because that is what its documentation ID binds to.
    /// </summary>
    /// <remarks>
    ///     <see cref="BannedSymbol_ResolvesToARealMember" /> cannot see this: reflection matches a
    ///     member by name regardless of signature, so an entry naming a method that only exists with
    ///     parameters resolves happily and bans nothing.
    ///     <para>
    ///         That is not hypothetical. <c>M:...DbContextOptionsBuilder.EnableSensitiveDataLogging</c>
    ///         sat in <c>infra.persistence.postgre/BannedSymbols.txt</c> and was inert — the method
    ///         takes an optional <c>bool</c>, so its declaration ID is
    ///         <c>EnableSensitiveDataLogging(System.Boolean)</c> and the bare form binds to nothing.
    ///         Confirmed by compiling a call to it and getting no diagnostic, then getting RS0030 for
    ///         the same call once the parameter list was added.
    ///     </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllEntries))]
    public void BannedSymbol_WithNoParameterList_NamesAParameterlessOverload(string project, string docId)
    {
        if (!docId.StartsWith("M:", StringComparison.Ordinal) || docId.Contains('(', StringComparison.Ordinal))
            return;

        string name = Regex.Replace(docId[2..], "``\\d+$", string.Empty);
        int lastDot = name.LastIndexOf('.');

        Type? declaringType = FindType(name[..lastDot]);

        Assert.True(declaringType is not null, $"{project}/BannedSymbols.txt declares '{docId}', whose "
                                               + "declaring type resolves to nothing.");

        MethodInfo[] overloads = declaringType!
            .GetMember(
                name[(lastDot + 1)..],
                BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
            .OfType<MethodInfo>()
            .ToArray();

        Assert.True(
            overloads.Any(overload => overload.GetParameters().Length == 0),
            $"{project}/BannedSymbols.txt declares '{docId}' with no parameter list, but every overload "
            + $"of that method takes parameters ({string.Join(" | ", overloads.Select(o => o.ToString()))}). "
            + "The documentation ID therefore binds to nothing and the analyzer reports no error for it. "
            + "Add the parameter list, e.g. '(System.Boolean)'.");
    }

    [Fact]
    public void BannedSymbolsFiles_ExistForEverySourceProject()
    {
        string[] projectsWithBans = RepositoryRoot()
            .GetFiles("BannedSymbols.txt", SearchOption.AllDirectories)
            .Select(f => Path.GetFileName(f.DirectoryName)!)
            .ToArray();

        Assert.Contains("domain", projectsWithBans);
        Assert.Contains("features", projectsWithBans);
        Assert.Contains("infra.persistence.postgre", projectsWithBans);
        Assert.Contains("api", projectsWithBans);
    }

    private static bool Resolve(string docId)
    {
        string kind = docId[..1];
        string rest = docId[2..];

        // Strip the parameter list and generic-arity marker; resolving the declaring type and the
        // member name is enough to catch a wrong namespace, a renamed type or a typo, which is the
        // failure mode. Exact overload matching is what the analyzer itself does.
        string withoutParameters = rest.Split('(')[0];
        withoutParameters = Regex.Replace(withoutParameters, "``\\d+$", string.Empty);

        if (kind == "T")
            return FindType(withoutParameters) is not null;

        int lastDot = withoutParameters.LastIndexOf('.');
        string typeName = withoutParameters[..lastDot];
        string memberName = withoutParameters[(lastDot + 1)..];

        Type? declaringType = FindType(typeName);

        return declaringType is not null && declaringType.GetMember(
            memberName,
            BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.FlattenHierarchy).Length > 0;
    }

    private static Type? FindType(string fullName)
    {
        EnsureReferencedAssembliesLoaded();

        return AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(fullName, throwOnError: false))
            .FirstOrDefault(type => type is not null);
    }

    /// <summary>
    ///     Walks the reference graph so lazily-loaded framework assemblies are present.
    /// </summary>
    /// <remarks>
    ///     Without this the test reports a false negative: ASP.NET Core types are not loaded into the
    ///     AppDomain until something touches them, so a perfectly valid ban would look unresolvable.
    ///     That is the opposite of the failure this test exists to catch, and it would train the next
    ///     person to delete a working entry.
    /// </remarks>
    private static void EnsureReferencedAssembliesLoaded()
    {
        HashSet<string> seen = [];
        Queue<Assembly> pending = new(AppDomain.CurrentDomain.GetAssemblies());

        while (pending.Count > 0)
        {
            Assembly assembly = pending.Dequeue();

            if (!seen.Add(assembly.FullName ?? assembly.GetName().Name ?? string.Empty))
                continue;

            foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
            {
                try
                {
                    pending.Enqueue(Assembly.Load(reference));
                }
                catch (FileNotFoundException)
                {
                    // A reference the test host does not ship. Nothing in a BannedSymbols file
                    // should point at one, and if it does the assertion will say so.
                }
                catch (FileLoadException)
                {
                }
            }
        }
    }

    private static DirectoryInfo RepositoryRoot()
    {
        DirectoryInfo directory = new(AppContext.BaseDirectory);

        while (directory.Parent is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
            directory = directory.Parent;

        return new DirectoryInfo(Path.Combine(directory.FullName, "src"));
    }
}
