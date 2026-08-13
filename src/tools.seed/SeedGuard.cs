using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using service.defaults;

namespace tools.seed;

/// <summary>
///     Refuses to seed unless a person asked for it, on a machine that opted in, against a loopback
///     database.
/// </summary>
/// <remarks>
///     <b>Why this exists at all is the answer to O-31.</b> Three mechanisms could have supplied
///     F00's rows and two are closed:
///     <list type="bullet">
///         <item><c>HasData</c> in a migration means authoring a migration, and design §5 permits
///         migrations only in F01c and F01d. It is also unbuildable against DEC-21: <c>HasData</c>
///         writes literal column values and bypasses the change tracker, so the audit interceptor
///         never runs — yet <c>created_at</c> and <c>created_by</c> are <c>NOT NULL</c> and their
///         setters are interceptor-only. And <c>HasData</c> rows are managed by model diffing, so
///         editing one later emits an <c>UpdateData</c> and removing one a <c>DeleteData</c> — a
///         physical delete of a reference row, which DEC-20 makes an unsanctioned path.</item>
///         <item>An endpoint is closed by DEC-17 and by the absence of authentication: every
///         <c>ICarterModule</c> in the dependency graph is auto-mounted and <c>Program.cs</c>
///         registers no authentication, so a seed endpoint would be an anonymous bulk write that
///         also, by construction, creates the rows an attacker needs to make other writes succeed.</item>
///         <item>A startup seeder — in <c>Program.cs</c>, the AppHost, or a test fixture — is closed
///         by this class's whole reason for being: it writes without anybody typing anything, and
///         "gated to Development" is the gate that a hurried first deployment satisfies by setting
///         <c>ASPNETCORE_ENVIRONMENT</c>.</item>
///     </list>
///     What is left is a console tool nobody runs by accident, and this guard is what makes "by
///     accident" true rather than hoped for. All three conditions must hold, and each refusal names
///     which one failed — a single generic refusal makes the tool feel broken rather than guarded.
///     <para>
///         The <c>--confirm</c> flag is read from <c>args</c> directly and is deliberately <em>not</em>
///         a configuration key: configuration is inheritable from a file, an environment variable or
///         a container definition, and this flag exists precisely to be the thing that is not.
///     </para>
///     <para>
///         The guard's honesty is <c>DeploymentGuard</c>'s: the loopback check is defeatable (O-16)
///         and <see cref="AllowSeedDataKey" /> is the real control, because it fails closed and has
///         to be set by a person.
///     </para>
/// </remarks>
public static class SeedGuard
{
    /// <summary>
    ///     The opt-in. Absent from every committed configuration file, exactly as
    ///     <c>DeploymentGuard.AllowAnonymousStubIdentityKey</c> is, and asserted absent by a test.
    /// </summary>
    public const string AllowSeedDataKey = "Attendance:AllowSeedData";

    /// <summary>The flag a person types. Nothing inherits it.</summary>
    public const string ConfirmFlag = "--confirm";

    /// <exception cref="InvalidOperationException">Any of the three conditions fails.</exception>
    public static void EnsureSeedingIsPermitted(IConfiguration configuration, IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(args);

        if (!args.Contains(ConfirmFlag, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Seeding requires {ConfirmFlag} on the command line. It writes reference data over whatever "
                + "is already there, so it is not something a script or a container entrypoint should be able "
                + "to trigger by inheriting configuration. Run: dotnet run --project src/tools.seed -- "
                + ConfirmFlag);
        }

        if (!bool.TryParse(configuration[AllowSeedDataKey], out bool allowed) || !allowed)
        {
            throw new InvalidOperationException(
                $"Seeding is not permitted. Set {AllowSeedDataKey}=true via user secrets or an environment "
                + "variable (Attendance__AllowSeedData=true). It is deliberately absent from every committed "
                + "configuration file so it cannot be inherited by a deployment.");
        }

        DeploymentGuard.EnsureLoopbackDatabase(configuration, "Seeding");
    }
}
