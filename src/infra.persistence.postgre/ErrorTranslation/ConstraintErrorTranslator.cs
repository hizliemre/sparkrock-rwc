using domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace infra.persistence.postgre.ErrorTranslation;

/// <summary>
///     Turns a provider constraint violation into a domain exception.
/// </summary>
/// <remarks>
///     This lives in the Postgres project because it cannot live anywhere else: <see cref="PostgresException" />
///     is an Npgsql type and <c>features</c> does not reference Npgsql (VC-23). A pure static function
///     over <c>(registry, DbUpdateException)</c> rather than a service, so the mapping decisions are
///     testable without a server.
/// </remarks>
internal static class ConstraintErrorTranslator
{
    /// <summary>
    ///     Translates <paramref name="source" />, or returns <see langword="null" /> to mean "rethrow
    ///     the original unchanged".
    /// </summary>
    /// <remarks>
    ///     Three of the four exits are <see langword="null" />, and each one is load-bearing for DEC-14:
    ///     <list type="bullet">
    ///         <item>
    ///             Not a <see cref="PostgresException" /> — which is also how
    ///             <see cref="DbUpdateConcurrencyException" /> survives. It derives from
    ///             <see cref="DbUpdateException" />, but its cause is a row count, not a server error,
    ///             and F07's <c>ex.Entries</c>/<c>ReloadAsync</c> recovery needs the original type (VC-29).
    ///         </item>
    ///         <item>No constraint name — there is nothing to look the violation up by.</item>
    ///         <item>
    ///             No row in the registry. Matching on <see cref="DbUpdateException" /> alone would
    ///             retry a permanent foreign-key or check violation until the bound is exhausted.
    ///         </item>
    ///     </list>
    /// </remarks>
    public static Exception? Translate(IConstraintErrorRegistry registry, DbUpdateException source)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(source);

        if (source.InnerException is not PostgresException postgresException)
            return null;

        string? constraintName = postgresException.ConstraintName;
        if (string.IsNullOrEmpty(constraintName))
            return null;

        if (!registry.TryResolve(constraintName, out ConstraintErrorMapping? mapping))
            return null;

        // The retryable case carries source.Entries through ConcurrencyConflictException. They are
        // the only route from features to the detach-or-reload a retry needs (VC-29); without them
        // identity resolution hands back the stale tracked instance and every attempt fails alike.
        return mapping.Retryable
            ? new ConcurrencyConflictException(constraintName, mapping.ErrorCode, mapping.Message, source)
            : new ConflictException(mapping.ErrorCode, mapping.Message);
    }
}
