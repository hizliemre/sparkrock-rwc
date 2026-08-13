using System.Diagnostics.CodeAnalysis;

namespace infra.persistence.postgre.ErrorTranslation;

/// <summary>
///     Maps a database constraint name to what its violation means. Lookup is by name and nothing
///     else: the name is pinned with <c>HasDatabaseName</c> in the migration that creates the index,
///     so the key and the constraint cannot drift apart (conventions §5).
/// </summary>
public interface IConstraintErrorRegistry
{
    /// <summary>
    ///     Resolves a constraint name to its mapping.
    /// </summary>
    /// <returns>
    ///     <see langword="false" /> when no feature has registered the constraint, which the
    ///     translator treats as "rethrow unchanged" rather than as a generic conflict.
    /// </returns>
    bool TryResolve(string constraintName, [MaybeNullWhen(false)] out ConstraintErrorMapping mapping);
}
