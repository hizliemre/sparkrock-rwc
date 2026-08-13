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
    ///     Reads the single <c>Host</c> value, or null if there is not exactly one loopback-shaped one.
    /// </summary>
    /// <remarks>
    ///     Deliberately strict rather than clever. A comma rejects multi-host strings, where only the
    ///     first entry might be loopback; a path separator rejects the unix-socket form, where a
    ///     directory is not a host at all.
    /// </remarks>
    private static string? ExtractHost(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return null;

        foreach (string part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = part.Split('=', 2);

            if (pair.Length != 2 || !pair[0].Trim().Equals("Host", StringComparison.OrdinalIgnoreCase))
                continue;

            string host = pair[1].Trim();

            if (host.Contains(',', StringComparison.Ordinal) || host.Contains('/', StringComparison.Ordinal))
                return host;

            return host;
        }

        return null;
    }
}
