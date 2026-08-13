using domain.Abstraction;

namespace domain.Import;

/// <summary>
///     A legacy row the import could not take as-is.
/// </summary>
/// <remarks>
///     Quarantine rather than coercion: a row that violates a new invariant is recorded with a reason
///     and left out, so the import neither fails wholesale nor silently invents data.
///     <para>
///         <see cref="AnomalyCode" /> is a code, never a copied value — in particular never the
///         source row's notes. <see cref="Detail" /> is bounded, and its content policy is an open
///         question owned by the import feature.
///     </para>
/// </remarks>
public sealed class LegacyImportAnomaly : BaseEntity
{
    /// <summary>Source table the row came from.</summary>
    public required string Entity { get; set; }

    /// <summary>Not unique — one legacy row can produce several anomalies.</summary>
    public int? LegacyId { get; set; }

    public required Guid BatchId { get; set; }

    public required string AnomalyCode { get; set; }

    public string? Detail { get; set; }
}
