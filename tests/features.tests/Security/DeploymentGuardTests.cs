using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using service.defaults;

namespace features.tests.Security;

/// <summary>
///     The guard between an anonymous build and real student data.
/// </summary>
/// <remarks>
///     Tested in both directions on purpose. A guard with only a happy-path test proves it can be
///     satisfied, not that it refuses — and refusing is its whole job.
/// </remarks>
public sealed class DeploymentGuardTests
{
    private const string LoopbackConnection = "Host=localhost;Port=5433;Database=x;Username=u;Password=p";

    private static IConfiguration Config(string? allow, string? connectionString)
    {
        Dictionary<string, string?> values = new()
        {
            [DeploymentGuard.AllowAnonymousStubIdentityKey] = allow,
            ["ConnectionStrings:sparkrock-rwc"] = connectionString
        };

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static FakeHostEnvironment Environment(string environmentName) =>
        new FakeHostEnvironment { EnvironmentName = environmentName };

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "features.tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }

    [Fact]
    public void EnsureStubIdentityIsPermitted_WhenAllThreeConditionsHold_DoesNotThrow()
    {
        DeploymentGuard.EnsureStubIdentityIsPermitted(
            Environment(Environments.Development), Config("true", LoopbackConnection));
    }

    [Fact]
    public void EnsureStubIdentityIsPermitted_WhenFlagAbsent_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => DeploymentGuard.EnsureStubIdentityIsPermitted(
            Environment(Environments.Development), Config(null, LoopbackConnection)));
    }

    [Fact]
    public void EnsureStubIdentityIsPermitted_WhenFlagFalse_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => DeploymentGuard.EnsureStubIdentityIsPermitted(
            Environment(Environments.Development), Config("false", LoopbackConnection)));
    }

    [Fact]
    public void EnsureStubIdentityIsPermitted_WhenNotDevelopment_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => DeploymentGuard.EnsureStubIdentityIsPermitted(
            Environment(Environments.Production), Config("true", LoopbackConnection)));
    }

    [Theory]
    [InlineData("Host=prod-db-01;Database=x")]
    [InlineData("Host=localhost,prod-db-01;Database=x")]
    [InlineData("Host=/var/run/postgresql;Database=x")]
    [InlineData("Database=x")]
    [InlineData("!!!not a connection string!!!")]
    public void EnsureStubIdentityIsPermitted_WhenHostIsNotLoopback_Throws(string connectionString)
    {
        Assert.Throws<InvalidOperationException>(() => DeploymentGuard.EnsureStubIdentityIsPermitted(
            Environment(Environments.Development), Config("true", connectionString)));
    }

    /// <summary>
    ///     Server is a documented Npgsql alias for Host and is the more common spelling in .NET
    ///     connection strings, so a hand-rolled scan for "Host" let a production host through a check
    ///     that reported loopback — without anyone intending to evade anything.
    /// </summary>
    [Theory]
    [InlineData("Server=prod-db-01;Database=x")]
    [InlineData("Host=localhost;Server=prod-db-01;Database=x")]
    [InlineData("Host=localhost;Host=prod-db-01;Database=x")]
    public void EnsureStubIdentityIsPermitted_WhenAnAliasOrDuplicateNamesARemoteHost_Throws(string connectionString)
    {
        Assert.Throws<InvalidOperationException>(() => DeploymentGuard.EnsureStubIdentityIsPermitted(
            Environment(Environments.Development), Config("true", connectionString)));
    }

    [Theory]
    [InlineData("Host=127.0.0.1;Database=x")]
    [InlineData("Host=::1;Database=x")]
    [InlineData("Server=localhost;Database=x")]
    public void EnsureStubIdentityIsPermitted_WhenHostIsAnotherLoopbackForm_DoesNotThrow(string connectionString)
    {
        DeploymentGuard.EnsureStubIdentityIsPermitted(
            Environment(Environments.Development), Config("true", connectionString));
    }

    /// <summary>
    ///     The opt-in must never be inheritable from a committed file, or the guard degrades to the
    ///     environment check it exists to be stronger than.
    /// </summary>
    [Fact]
    public void NoCommittedAppSettingsFileContainsTheOptIn()
    {
        DirectoryInfo repositoryRoot = new(AppContext.BaseDirectory);

        while (repositoryRoot.Parent is not null && !Directory.Exists(Path.Combine(repositoryRoot.FullName, "src")))
            repositoryRoot = repositoryRoot.Parent;

        string[] settingsFiles = Directory.GetFiles(
            Path.Combine(repositoryRoot.FullName, "src"), "appsettings*.json", SearchOption.AllDirectories);

        Assert.NotEmpty(settingsFiles);

        foreach (string file in settingsFiles.Where(f => !f.Contains("/bin/", StringComparison.Ordinal)
                                                         && !f.Contains("/obj/", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("AllowAnonymousStubIdentity", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }
}
