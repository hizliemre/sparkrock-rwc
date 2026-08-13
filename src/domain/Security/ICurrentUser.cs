namespace domain.Security;

/// <summary>
///     The acting identity for the current request.
/// </summary>
/// <remarks>
///     Carries authorisation scope, not just identity. The scope members exist from day one
///     deliberately: retrofitting them later would mean editing every query in every slice, and the
///     failure mode of forgetting one is a silent cross-tenant read rather than an error.
///     <para>
///         Registered scoped. The stub implementation returns <c>IsSystemAdmin = true</c>, so tests
///         must inject a non-admin or the entire authorisation surface goes unexercised.
///     </para>
/// </remarks>
public interface ICurrentUser
{
    Guid UserId { get; }

    string DisplayName { get; }

    /// <summary>Schools this identity may read and write. Ignored when <see cref="IsSystemAdmin" />.</summary>
    IReadOnlyCollection<Guid> AuthorizedSchoolIds { get; }

    bool IsSystemAdmin { get; }
}
