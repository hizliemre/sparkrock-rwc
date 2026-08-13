using System.Data.Common;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace service.defaults;

/// <summary>
///     Refuses to start an anonymous build anywhere it could reach real student data.
/// </summary>
/// <remarks>
///     With the stub identity registered every endpoint is anonymous and every school's roster,
///     attendance history and alert list is world-readable.
///     <para>
///         The guard fails closed on an explicit opt-in rather than on environment inference.
///         <c>ASPNETCORE_ENVIRONMENT=Development</c> alone is not sufficient, because that is exactly
///         what a hurried first deployment sets.
///     </para>
///     <para>
///         The loopback check is a second line and is defeatable — a tunnel, an <c>/etc/hosts</c>
///         entry or a multi-host connection string all get past it. <b>The flag is the control</b>,
///         because it fails closed and has to be typed by a person. A determined operator can still
///         satisfy all three conditions against production; real authentication is what closes that,
///         and this is recorded rather than claimed fixed.
///     </para>
/// </remarks>
public static class DeploymentGuard
{
    public const string AllowAnonymousStubIdentityKey = "Attendance:AllowAnonymousStubIdentity";

    private const string ConnectionStringName = "sparkrock-rwc";

    private static readonly string[] LoopbackHosts = ["localhost", "127.0.0.1", "::1", "[::1]"];

    /// <summary>Every keyword Npgsql accepts for the server address.</summary>
    private static readonly string[] HostKeywords = ["Host", "Server"];

    /// <exception cref="InvalidOperationException">Any of the three conditions fails.</exception>
    public static void EnsureStubIdentityIsPermitted(IHostEnvironment environment, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!bool.TryParse(configuration[AllowAnonymousStubIdentityKey], out bool allowed) || !allowed)
        {
            throw new InvalidOperationException(
                $"The anonymous stub identity is not permitted. Set {AllowAnonymousStubIdentityKey}=true via user "
                + "secrets or an environment variable. It is deliberately absent from every committed configuration "
                + "file so it cannot be inherited by a deployment.");
        }

        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"The anonymous stub identity is only permitted in Development; the environment is "
                + $"'{environment.EnvironmentName}'.");
        }

        string? host = ExtractHost(configuration.GetConnectionString(ConnectionStringName));

        if (host is null || !LoopbackHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The anonymous stub identity is only permitted against a loopback database. The connection string "
                + $"'{ConnectionStringName}' resolves to host '{host ?? "<none>"}'.");
        }
    }

    /// <summary>
    ///     Reads the effective host, or null if the string declares none or cannot be parsed.
    /// </summary>
    /// <remarks>
    ///     Parsed with <see cref="DbConnectionStringBuilder" /> rather than split by hand. A manual
    ///     split got three things wrong, each of which let a production host through a check that
    ///     reported loopback:
    ///     <list type="bullet">
    ///         <item><c>Server</c> is a documented Npgsql alias for <c>Host</c>, and is the more
    ///         common spelling in .NET connection strings, so this was reachable without anyone
    ///         intending to evade the guard.</item>
    ///         <item>Npgsql resolves the <em>last</em> occurrence of a duplicated key; a first-match
    ///         scan reads the wrong one.</item>
    ///         <item>A quoted value containing a semicolon fragments under a naive split.</item>
    ///     </list>
    ///     Multi-host and unix-socket forms return the raw value, which then fails the loopback
    ///     comparison — rejection happens there, in one place, rather than in a branch here that
    ///     returned the value either way and so rejected nothing.
    /// </remarks>
    private static string? ExtractHost(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return null;

        DbConnectionStringBuilder builder = new();

        try
        {
            builder.ConnectionString = connectionString;
        }
        catch (ArgumentException)
        {
            return null;
        }

        // Collect every spelling rather than picking one. Npgsql treats Server as an alias for Host
        // and resolves the last occurrence, so "Host=localhost;Server=prod" connects to prod — and a
        // guard that reads the first key reports loopback. Rather than reimplement that precedence,
        // treat any disagreement as unresolvable: for a control whose whole job is refusing, an
        // ambiguous answer is a rejection, not a guess.
        string[] hosts = HostKeywords
            .Select(key => builder.TryGetValue(key, out object? value) ? value as string : null)
            .Where(host => !string.IsNullOrWhiteSpace(host))
            .Select(host => host!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return hosts.Length == 1 ? hosts[0] : null;
    }
}
