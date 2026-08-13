using domain.Abstraction;
using domain.Exceptions;

namespace domain.Security;

/// <summary>
///     Tenant scoping, applied explicitly per query.
/// </summary>
/// <remarks>
///     Not a query filter, and cannot be one: EF Core 8 allows a single filter per entity type and
///     silently replaces it on a second call, so soft delete already owns that slot. A filter would
///     also be wrong on the two entities that matter most, because absence counts deliberately span
///     schools within a school year.
///     <para>
///         The cost is that omission is silent. A read that forgets <see cref="WhereAuthorized" />
///         returns every school's rows and no test fails unless the identity under test is a
///         non-admin.
///     </para>
/// </remarks>
public static class AuthorizationExtensions
{
    /// <summary>
    ///     Restricts a query to the schools this identity may see.
    /// </summary>
    public static IQueryable<TEntity> WhereAuthorized<TEntity>(
        this IQueryable<TEntity> source,
        ICurrentUser currentUser)
        where TEntity : class, ISchoolScoped
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(currentUser);

        // The short-circuit is load-bearing rather than an optimisation: an empty scope matches no
        // rows, so an administrator with no schools listed would otherwise be shown nothing.
        if (currentUser.IsSystemAdmin)
            return source;

        return source.Where(entity => currentUser.AuthorizedSchoolIds.Contains(entity.SchoolId));
    }

    /// <summary>
    ///     Asserts this identity may act on a school, throwing <see cref="NotFoundException" /> if not.
    /// </summary>
    /// <remarks>
    ///     404 rather than 403, always. A distinguishable status tells an unauthorised caller that the
    ///     school exists, which is the existence oracle tenant scoping is meant to close. Privilege
    ///     failures on globally visible resources are the separate case that uses
    ///     <see cref="ForbiddenException" />.
    /// </remarks>
    public static void EnsureAuthorized(this ICurrentUser currentUser, Guid schoolId, string notFoundErrorCode)
    {
        ArgumentNullException.ThrowIfNull(currentUser);

        if (currentUser.IsSystemAdmin || currentUser.AuthorizedSchoolIds.Contains(schoolId))
            return;

        throw new NotFoundException(notFoundErrorCode);
    }
}
