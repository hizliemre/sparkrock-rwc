namespace domain.Abstraction;

/// <summary>
///     An entity carried across from the legacy database, keyed there by an integer identity.
/// </summary>
/// <remarks>
///     The import matches on <see cref="LegacyId" />, so its index is unique rather than merely
///     indexed: a plain index lets a re-run — the normal outcome of a failed cutover — duplicate
///     every row and silently double every absence recount.
///     <para>
///         Import- and reconciliation-internal. It never appears in a route, a query parameter or a
///         response, because it is a sequential integer and exposing it restores exactly the
///         enumeration that Guid keys prevent.
///     </para>
/// </remarks>
public interface ILegacyEntity
{
    int? LegacyId { get; set; }
}
