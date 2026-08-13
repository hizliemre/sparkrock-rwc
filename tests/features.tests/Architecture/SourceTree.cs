namespace features.tests.Architecture;

/// <summary>
///     Locates the repository on disk for the architecture checks that assert on source text rather
///     than on a loaded type.
/// </summary>
/// <remarks>
///     The root is found by walking up from <see cref="AppContext.BaseDirectory" /> until a directory
///     containing <c>src</c> appears, never by a path baked into the test. A hardcoded path is the
///     silent-no-op failure this suite exists to prevent: it resolves to nothing on every machine but
///     the one it was written on, and a scan over zero files asserts nothing while reporting green.
///     <para>
///         Build output is excluded by path segment using <see cref="Path.DirectorySeparatorChar" />,
///         because a literal <c>/</c> filter is a no-op on Windows — where the scan would then read
///         every copy of every source file under <c>bin</c> and <c>obj</c>, including generated ones.
///     </para>
/// </remarks>
internal static class SourceTree
{
    public static DirectoryInfo Root()
    {
        DirectoryInfo directory = new(AppContext.BaseDirectory);

        while (directory.Parent is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
            directory = directory.Parent;

        if (!Directory.Exists(Path.Combine(directory.FullName, "src")))
            throw new InvalidOperationException(
                $"No repository root found above '{AppContext.BaseDirectory}'. Every source-text check "
                + "below this point would scan nothing and pass, so this fails loudly instead.");

        return directory;
    }

    /// <summary>
    ///     Every hand-written C# file under <c>src</c>, build output excluded.
    /// </summary>
    public static FileInfo[] ProductionSources() => Sources(new DirectoryInfo(Path.Combine(Root().FullName, "src")));

    public static FileInfo[] Sources(DirectoryInfo root) =>
        root.GetFiles("*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file.FullName))
            .OrderBy(file => file.FullName, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    ///     Path relative to the repository root, with forward slashes, for assertion messages that
    ///     should read the same on every machine.
    /// </summary>
    public static string RelativePath(FileInfo file) =>
        Path.GetRelativePath(Root().FullName, file.FullName).Replace(Path.DirectorySeparatorChar, '/');

    private static bool IsBuildOutput(string path)
    {
        string separator = Path.DirectorySeparatorChar.ToString();

        return path.Contains($"{separator}bin{separator}", StringComparison.Ordinal)
               || path.Contains($"{separator}obj{separator}", StringComparison.Ordinal);
    }
}
