namespace domain.Security;

/// <summary>
///     Lets the importer attribute writes to a reserved identity and preserve legacy timestamps.
/// </summary>
/// <remarks>
///     Registered scoped and inactive in the request pipeline; only the importer calls
///     <see cref="Begin" />. While active the interceptor attributes writes to
///     <see cref="ActingUserId" /> and leaves an already-populated <c>CreatedAt</c> alone — otherwise
///     every imported row would claim to have been created at import time, destroying the only
///     genuine audit data the legacy system had.
/// </remarks>
public interface IAuditOverride
{
    bool IsActive { get; }

    Guid ActingUserId { get; }

    /// <summary>Activates the override until the returned scope is disposed.</summary>
    IDisposable Begin(Guid actingUserId);
}
