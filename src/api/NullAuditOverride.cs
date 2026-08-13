using domain.Security;

namespace api;

/// <summary>
///     The audit override the HTTP pipeline gets: permanently inactive, and impossible to activate.
/// </summary>
/// <remarks>
///     <see cref="IAuditOverride" /> exists for one caller that does not exist yet. While active it
///     attributes writes to an arbitrary <see cref="IAuditOverride.ActingUserId" /> and leaves an
///     already-populated <c>CreatedAt</c> alone, so the interceptor's two guarantees — who changed
///     this row, and when it first appeared — both become whatever the caller says.
///     <para>
///         <see cref="IAuditOverride.Begin" /> is public on a public interface, and the real
///         implementation was registered scoped in the request pipeline. Any handler could therefore
///         take the dependency and forge attribution on a record it wrote, in a system whose only
///         accountability control is the audit columns. Nothing did — but "nothing calls it" is not a
///         control, and the importer that needs it will construct its own from its own composition
///         root rather than inherit the request pipeline's.
///     </para>
///     <para>
///         A null object rather than an absent registration: the interceptor takes
///         <see cref="IAuditOverride" /> as a constructor dependency, so leaving it unregistered fails
///         at resolution instead of failing closed.
///     </para>
/// </remarks>
internal sealed class NullAuditOverride : IAuditOverride
{
    public bool IsActive => false;

    public Guid ActingUserId => Guid.Empty;

    /// <exception cref="InvalidOperationException">Always.</exception>
    public IDisposable Begin(Guid actingUserId) =>
        throw new InvalidOperationException(
            "Audit attribution cannot be overridden from the request pipeline. The override exists for the "
            + "importer, which supplies its own from its own composition root.");
}
