using Microsoft.Extensions.Configuration;
using service.defaults;
using tools.seed;

namespace features.tests.Seed;

/// <summary>
///     The seeder's fail-closed preconditions, and the loopback check it shares with
///     <see cref="DeploymentGuard" />.
/// </summary>
/// <remarks>
///     Tested in both directions, for <c>DeploymentGuardTests</c>'s reason: a guard with only a
///     happy-path test proves it can be satisfied, not that it refuses — and refusing is its whole
///     job.
///     <para>
///         Each refusal is asserted to <em>name</em> the condition that failed. Three conditions and
///         one generic message makes the tool feel broken rather than guarded, and the developer then
///         goes looking for the bug instead of reading the sentence.
///     </para>
/// </remarks>
public sealed class SeedGuardTests
{
    private const string LoopbackConnection = "Host=localhost;Port=5433;Database=x;Username=u;Password=p";

    private const string RemoteConnection = "Host=prod-db-01;Database=x";

    private static readonly string[] Confirmed = [SeedGuard.ConfirmFlag];

    private static IConfiguration Config(string? allow, string? connectionString) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [SeedGuard.AllowSeedDataKey] = allow,
            ["ConnectionStrings:sparkrock-rwc"] = connectionString
        }).Build();

    [Fact]
    public void EnsureSeedingIsPermitted_WhenAllThreeConditionsHold_DoesNotThrow() =>
        SeedGuard.EnsureSeedingIsPermitted(Config("true", LoopbackConnection), Confirmed);

    /// <summary>
    ///     The flag a person types. It is read from <c>args</c> and is deliberately not a
    ///     configuration key — configuration is inheritable from a file, an environment variable or a
    ///     container definition, and this is the condition that exists to be the one that is not.
    /// </summary>
    [Fact]
    public void EnsureSeedingIsPermitted_WhenConfirmFlagAbsent_Throws()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => SeedGuard.EnsureSeedingIsPermitted(Config("true", LoopbackConnection), []));

        Assert.Contains(SeedGuard.ConfirmFlag, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>A near miss must not pass: the flag is matched exactly, not by prefix.</summary>
    [Theory]
    [InlineData("--Confirm")]
    [InlineData("-confirm")]
    [InlineData("confirm")]
    [InlineData("--confirm=true")]
    public void EnsureSeedingIsPermitted_WhenConfirmFlagIsMisspelled_Throws(string argument) =>
        Assert.Throws<InvalidOperationException>(
            () => SeedGuard.EnsureSeedingIsPermitted(Config("true", LoopbackConnection), [argument]));

    [Fact]
    public void EnsureSeedingIsPermitted_WhenAllowSeedDataFlagAbsent_Throws()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => SeedGuard.EnsureSeedingIsPermitted(Config(null, LoopbackConnection), Confirmed));

        Assert.Contains(SeedGuard.AllowSeedDataKey, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("yes")]
    [InlineData("")]
    public void EnsureSeedingIsPermitted_WhenAllowSeedDataIsNotTrue_Throws(string allow) =>
        Assert.Throws<InvalidOperationException>(
            () => SeedGuard.EnsureSeedingIsPermitted(Config(allow, LoopbackConnection), Confirmed));

    [Theory]
    [InlineData(RemoteConnection)]
    [InlineData("Server=prod-db-01;Database=x")]
    [InlineData("Host=localhost;Server=prod-db-01;Database=x")]
    [InlineData("Host=localhost,prod-db-01;Database=x")]
    [InlineData(null)]
    public void EnsureSeedingIsPermitted_WhenDatabaseHostIsNotLoopback_Throws(string? connectionString)
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => SeedGuard.EnsureSeedingIsPermitted(Config("true", connectionString), Confirmed));

        Assert.Contains("loopback", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     The order matters: the connection string is never even read until the two typed conditions
    ///     hold, so a misconfigured tool reports the thing the developer can fix.
    /// </summary>
    [Fact]
    public void EnsureSeedingIsPermitted_WhenNothingIsSatisfied_ReportsTheConfirmFlagFirst()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => SeedGuard.EnsureSeedingIsPermitted(Config(null, RemoteConnection), []));

        Assert.Contains(SeedGuard.ConfirmFlag, exception.Message, StringComparison.Ordinal);
    }

    // ---- The extracted loopback check (T00-09) ---------------------------------------------

    /// <summary>
    ///     <c>DeploymentGuard.EnsureLoopbackDatabase</c> is the loopback test lifted out of
    ///     <c>EnsureStubIdentityIsPermitted</c> so the seeder shares it rather than re-implementing it.
    /// </summary>
    /// <remarks>
    ///     Re-implementing was the tempting option and is why the parser looks the way it does: three
    ///     hand-rolled variants each let a production host through a check that reported loopback —
    ///     the <c>Server=</c> alias, duplicate-key precedence, and a quoted value containing a
    ///     semicolon. <c>ExtractHost</c> stays private; the check is the public surface, not the parser.
    ///     <para>
    ///         The existing <c>DeploymentGuardTests</c> are unedited, which is what makes this a
    ///         refactor rather than a behaviour change quietly rewritten to agree with itself.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("Host=127.0.0.1;Database=x")]
    [InlineData("Host=::1;Database=x")]
    [InlineData("Server=localhost;Database=x")]
    public void EnsureLoopbackDatabase_WhenHostIsLoopback_DoesNotThrow(string connectionString) =>
        DeploymentGuard.EnsureLoopbackDatabase(Config("true", connectionString), "A test");

    [Theory]
    [InlineData("Host=prod-db-01;Database=x")]
    [InlineData("Server=prod-db-01;Database=x")]
    [InlineData("Host=localhost;Host=prod-db-01;Database=x")]
    [InlineData("Host=/var/run/postgresql;Database=x")]
    [InlineData("Database=x")]
    [InlineData("!!!not a connection string!!!")]
    public void EnsureLoopbackDatabase_WhenHostIsNotLoopback_Throws(string connectionString) =>
        Assert.Throws<InvalidOperationException>(
            () => DeploymentGuard.EnsureLoopbackDatabase(Config("true", connectionString), "A test"));

    /// <summary>The caller names the subject, so the message says which control refused.</summary>
    [Fact]
    public void EnsureLoopbackDatabase_NamesTheCallerInTheRefusal()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => DeploymentGuard.EnsureLoopbackDatabase(Config("true", RemoteConnection), "Seeding"));

        Assert.StartsWith("Seeding is only permitted against a loopback database.",
            exception.Message, StringComparison.Ordinal);
    }
}
