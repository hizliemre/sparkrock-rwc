using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace features.tests.Architecture;

/// <summary>
///     Asserts <c>HasQueryFilter</c> is declared at exactly one place in the whole of <c>src</c>: the
///     reflective soft-delete loop in <c>SparkrockRwcDbContext.OnModelCreating</c>.
/// </summary>
/// <remarks>
///     Conventions §7 bans <c>HasQueryFilter</c> outside that loop, and <c>BannedApiAnalyzers</c>
///     cannot express the rule — it bans a symbol everywhere in a project or nowhere, and the one
///     sanctioned call lives in the same project as everything it must forbid. A suppression at the
///     sanctioned site would work mechanically and teach the wrong lesson: the next declaration would
///     copy the suppression along with the call.
///     <para>
///         The rule matters because the failure is silent. EF Core 8 permits <b>one</b> query filter
///         per entity type, and the reflective loop runs after
///         <c>ApplyConfigurationsFromAssembly</c> — so a tenant filter declared in an
///         <c>IEntityTypeConfiguration</c> is overwritten by the soft-delete filter with no error, no
///         warning and no diagnostic. Every query then reads across schools while the configuration
///         file states, truthfully as written, that it does not. Tenant scope is an explicit
///         <c>.WhereAuthorized()</c> predicate for that reason.
///     </para>
///     <para>
///         The scan is syntactic rather than semantic: it matches the identifier
///         <c>HasQueryFilter</c> anywhere in the syntax tree, so it catches a direct call, a method
///         group, an extension invocation, and a <c>nameof</c>. Identifiers are tokens, so a mention
///         inside a comment or an XML doc — this one included — is trivia and does not match. String
///         literals are matched separately, because a reflective invocation by name bypasses every
///         other form of detection and is precisely the shape the sanctioned site itself uses to build
///         its filter.
///     </para>
///     <para>
///         <see cref="Detector_FindsAPlantedDeclaration" /> runs the same detector over a planted
///         violation in a temporary directory. Without it, a detector that silently matched nothing —
///         a wrong root, a changed layout, an <c>*.cs</c> glob that stopped globbing — would report
///         zero usages and the "no unsanctioned usage" assertion would pass on emptiness.
///     </para>
/// </remarks>
public sealed class QueryFilterTests
{
    private const string SanctionedFile = "src/infra.persistence.postgre/SparkrockRwcDbContext.cs";
    private const string SanctionedMethod = "OnModelCreating";

    [Fact]
    public void HasQueryFilter_IsDeclaredOnlyInTheReflectiveSoftDeleteLoop()
    {
        Usage[] usages = FindUsages(SourceTree.ProductionSources());

        string[] unsanctioned = usages
            .Where(usage => usage.File != SanctionedFile || usage.Method != SanctionedMethod)
            .Select(usage => $"{usage.File}({usage.Line}) in {usage.Method}")
            .ToArray();

        Assert.True(
            unsanctioned.Length == 0,
            "HasQueryFilter is declared outside the reflective soft-delete loop: "
            + string.Join(", ", unsanctioned)
            + ". EF Core 8 allows one filter per entity type and the loop overwrites whatever "
            + "configuration declared, so this filter is silently discarded. Tenant scope is an "
            + "explicit .WhereAuthorized() predicate (conventions §7, O-39).");
    }

    /// <summary>
    ///     The sanctioned call must still be there. If the loop is deleted or renamed, soft delete
    ///     stops being applied at all — and the assertion above would go green on an empty result.
    /// </summary>
    [Fact]
    public void HasQueryFilter_SanctionedDeclarationStillExists()
    {
        Usage[] usages = FindUsages(SourceTree.ProductionSources());

        Usage sanctioned = Assert.Single(usages);

        Assert.Equal(SanctionedFile, sanctioned.File);
        Assert.Equal(SanctionedMethod, sanctioned.Method);
    }

    [Fact]
    public void ProductionSources_AreActuallyBeingScanned()
    {
        FileInfo[] sources = SourceTree.ProductionSources();

        Assert.True(sources.Length > 20, $"Only {sources.Length} source files found under src. "
                                         + "A scan over nothing passes every assertion in this class.");

        Assert.Contains(sources, file => SourceTree.RelativePath(file) == SanctionedFile);
    }

    [Fact]
    public void Detector_FindsAPlantedDeclaration()
    {
        DirectoryInfo planted = new(Path.Combine(Path.GetTempPath(), $"qf-probe-{Guid.NewGuid():N}"));
        planted.Create();

        try
        {
            File.WriteAllText(
                Path.Combine(planted.FullName, "Configuration.cs"),
                """
                namespace probe;

                internal sealed class Configuration
                {
                    public void Configure(EntityTypeBuilder<School> builder)
                    {
                        builder.HasQueryFilter(school => school.SchoolId == CurrentSchool);
                    }
                }
                """);

            Usage[] usages = FindUsages(SourceTree.Sources(planted));

            Usage found = Assert.Single(usages);

            Assert.Equal("Configure", found.Method);
        }
        finally
        {
            planted.Delete(recursive: true);
        }
    }

    /// <summary>
    ///     A reflective invocation by name is the one form no identifier match can see, and the
    ///     sanctioned site is itself built out of reflection — so the detector reads literals too.
    /// </summary>
    [Fact]
    public void Detector_FindsAReflectiveDeclarationByName()
    {
        DirectoryInfo planted = new(Path.Combine(Path.GetTempPath(), $"qf-probe-{Guid.NewGuid():N}"));
        planted.Create();

        try
        {
            File.WriteAllText(
                Path.Combine(planted.FullName, "Sneaky.cs"),
                """
                namespace probe;

                internal sealed class Sneaky
                {
                    public void Configure(object builder)
                    {
                        builder.GetType().GetMethod("HasQueryFilter")!.Invoke(builder, []);
                    }
                }
                """);

            Assert.Single(FindUsages(SourceTree.Sources(planted)));
        }
        finally
        {
            planted.Delete(recursive: true);
        }
    }

    private static Usage[] FindUsages(IEnumerable<FileInfo> files)
    {
        List<Usage> usages = [];

        foreach (FileInfo file in files)
        {
            SyntaxNode root = CSharpSyntaxTree.ParseText(File.ReadAllText(file.FullName)).GetRoot();

            IEnumerable<SyntaxToken> identifiers = root
                .DescendantTokens()
                .Where(token => token.IsKind(SyntaxKind.IdentifierToken)
                                && token.ValueText == nameof(Microsoft.EntityFrameworkCore.Metadata.Builders
                                    .EntityTypeBuilder.HasQueryFilter));

            IEnumerable<SyntaxToken> literals = root
                .DescendantTokens()
                .Where(token => token.IsKind(SyntaxKind.StringLiteralToken)
                                && token.ValueText.Contains("HasQueryFilter", StringComparison.Ordinal));

            foreach (SyntaxToken token in identifiers.Concat(literals))
            {
                usages.Add(new Usage(
                    Describe(file),
                    token.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    EnclosingMethod(token)));
            }
        }

        return [.. usages];
    }

    /// <summary>
    ///     Repository-relative path for files under the repository, bare filename for the planted
    ///     probes in the temp directory — where a relative path would be a run of <c>..</c> segments.
    /// </summary>
    private static string Describe(FileInfo file)
    {
        string relative = SourceTree.RelativePath(file);

        return relative.StartsWith("..", StringComparison.Ordinal) ? file.Name : relative;
    }

    private static string EnclosingMethod(SyntaxToken token) =>
        token.Parent?
            .AncestorsAndSelf()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault()?
            .Identifier.ValueText
        ?? "<no enclosing method>";

    private sealed record Usage(string File, int Line, string Method);
}
