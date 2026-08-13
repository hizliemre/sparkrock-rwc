using System.Text.Json;

namespace features.tests.Security;

/// <summary>
///     Host filtering, asserted on the committed configuration rather than on a running host.
/// </summary>
/// <remarks>
///     <c>"AllowedHosts": "*"</c> disables the host-filtering middleware entirely. The API then answers
///     to any <c>Host</c> header, which is what makes DNS rebinding and cache-poisoning against a
///     link-generating endpoint work, and — with the anonymous stub identity registered — the target
///     is every school's roster. The wildcard is also the template default, so nobody ever chose it.
/// </remarks>
public sealed class HostFilteringTests
{
    private const string AllowedHosts = "AllowedHosts";

    private static IEnumerable<string> SettingsFiles() =>
        RepositoryFiles.ConfigurationSources()
            .Where(file => Path.GetFileName(file).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase));

    private static string? AllowedHostsIn(string file)
    {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(file),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        return document.RootElement.TryGetProperty(AllowedHosts, out JsonElement value)
            ? value.GetString()
            : null;
    }

    [Fact]
    public void NoCommittedSettingsFileAllowsEveryHost()
    {
        foreach (string file in SettingsFiles())
            Assert.NotEqual("*", AllowedHostsIn(file));
    }

    /// <summary>
    ///     Absent is the same as wildcard: <c>HostFilteringOptions</c> defaults to allowing everything
    ///     when the key is missing, so the base file has to state a list.
    /// </summary>
    [Fact]
    public void TheBaseSettingsFileStatesAnExplicitHostList()
    {
        string baseSettings = Path.Combine(RepositoryFiles.Root().FullName, "src", "api", "appsettings.json");

        string? allowedHosts = AllowedHostsIn(baseSettings);

        Assert.False(string.IsNullOrWhiteSpace(allowedHosts));
        Assert.DoesNotContain("*", allowedHosts, StringComparison.Ordinal);
        Assert.NotEmpty(allowedHosts!.Split(';', StringSplitOptions.RemoveEmptyEntries));
    }
}
