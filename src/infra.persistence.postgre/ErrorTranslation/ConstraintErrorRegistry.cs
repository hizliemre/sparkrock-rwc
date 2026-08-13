using System.Diagnostics.CodeAnalysis;

namespace infra.persistence.postgre.ErrorTranslation;

/// <summary>
///     The default <see cref="IConstraintErrorRegistry" />, backed by a fixed table.
/// </summary>
/// <remarks>
///     F01a ships <see cref="Empty" />. Every constraint in conventions §5's table belongs to an
///     entity that does not exist yet, and a speculative row would pin a name no migration has
///     created — a key that matches nothing translates nothing, silently. The feature that authors a
///     constraint adds its row in the same commit as the <c>HasDatabaseName</c> that creates it.
/// </remarks>
public sealed class ConstraintErrorRegistry : IConstraintErrorRegistry
{
    private readonly Dictionary<string, ConstraintErrorMapping> _mappings;

    public ConstraintErrorRegistry(IReadOnlyDictionary<string, ConstraintErrorMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);

        // Copied, and copied with an ordinal comparer. Postgres folds unquoted identifiers to
        // lower case and HasDatabaseName pins the exact string, so a case-insensitive registry
        // would accept a key no constraint can ever produce.
        _mappings = new Dictionary<string, ConstraintErrorMapping>(mappings.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, ConstraintErrorMapping> mapping in mappings)
            _mappings.Add(mapping.Key, mapping.Value);
    }

    /// <summary>A registry that resolves nothing, so every constraint violation rethrows unchanged.</summary>
    public static ConstraintErrorRegistry Empty { get; } = new(new Dictionary<string, ConstraintErrorMapping>());

    public bool TryResolve(string constraintName, [MaybeNullWhen(false)] out ConstraintErrorMapping mapping) =>
        _mappings.TryGetValue(constraintName, out mapping);
}
