namespace features.tests.Security;

/// <summary>
///     Enumerates tracked files, for the checks that assert on what is committed rather than on what
///     a type does.
/// </summary>
/// <remarks>
///     Build output is excluded by path segment using <see cref="Path.DirectorySeparatorChar" />, not
///     by a hardcoded <c>/</c>. A forward-slash filter is a no-op on Windows, where the same scan then
///     reads every copy of every settings file under <c>bin</c> and <c>obj</c> — including ones
///     generated from sources this check is not meant to police, and including ones that legitimately
///     carry a developer's local opt-in.
/// </remarks>
internal static class RepositoryFiles
{
    /// <summary>
    ///     Every committed file a deployment-affecting setting could be written into.
    /// </summary>
    /// <remarks>
    ///     <c>appsettings*.json</c> alone is too narrow to be the control it is written as.
    ///     <c>launchSettings.json</c> is tracked and its <c>environmentVariables</c> block is the
    ///     obvious place to put a flag that makes <c>dotnet run</c> work — and everyone who clones the
    ///     repository then inherits it. MSBuild property files, Dockerfiles and compose files reach the
    ///     process environment just as directly.
    /// </remarks>
    public static string[] ConfigurationSources()
    {
        string[] patterns =
        [
            "appsettings*.json",
            "launchSettings.json",
            "*.props",
            "Dockerfile*",
            "docker-compose*"
        ];

        return patterns
            .SelectMany(pattern => Directory.GetFiles(Root().FullName, pattern, SearchOption.AllDirectories))
            .Where(IsTracked)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public static DirectoryInfo Root()
    {
        DirectoryInfo directory = new(AppContext.BaseDirectory);

        while (directory.Parent is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
            directory = directory.Parent;

        return directory;
    }

    private static bool IsTracked(string path)
    {
        string separator = Path.DirectorySeparatorChar.ToString();

        return !path.Contains($"{separator}bin{separator}", StringComparison.Ordinal)
               && !path.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
               && !path.Contains($"{separator}.git{separator}", StringComparison.Ordinal);
    }
}
