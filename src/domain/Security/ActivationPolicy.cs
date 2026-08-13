using domain.Abstraction;
using domain.Exceptions;

namespace domain.Security;

/// <summary>
///     Which privilege class governs an entity's activation state.
/// </summary>
/// <remarks>
///     Named at every call site even when the answer is <see cref="SchoolScope" />, so that a slice
///     changing <c>IsActive</c> without stating its privilege class is a slice with no call to
///     <see cref="ActivationPolicy.Apply" /> — which is greppable.
/// </remarks>
public enum ActivationPrivilege
{
    /// <summary>
    ///     School membership is the whole rule, and the caller has already satisfied it by passing
    ///     <see cref="AuthorizationExtensions.EnsureAuthorized" /> at load. No further check.
    /// </summary>
    SchoolScope,

    /// <summary>Only a system administrator may change the state (DEC-20).</summary>
    SystemAdmin
}

/// <summary>
///     The one place the DEC-20 activation privilege rule lives.
/// </summary>
/// <remarks>
///     DEC-20 attaches the privilege to the <c>IsActive</c> <em>transition</em>, not to the
///     <c>DELETE</c> endpoint: <c>PUT { "isActive": false }</c> reaches the same state, so a
///     per-endpoint check leaves half of it unguarded (O-12). Both the update and the deactivate
///     slice of every reference aggregate route their activation change through here, and neither
///     contains its own <c>if (entity.IsActive != requested)</c> comparison.
///     <para>
///         <b>Considered and rejected: enforcing this in the persistence layer.</b> An interceptor
///         refusing any <c>Modified</c> entry whose <c>IsActive</c> changed would be total rather than
///         disciplined, but it fires inside <c>SaveChangesAsync</c> where the save pipeline's retry
///         loop inspects exception types; it sees state rather than intent, so it cannot distinguish a
///         deactivation from the import legitimately creating an inactive row; and it would put an
///         authorisation rule where no handler test can reach it.
///     </para>
/// </remarks>
public static class ActivationPolicy
{
    /// <summary>
    ///     Applies a requested activation state. Returns <c>true</c> when the entity changed.
    /// </summary>
    /// <remarks>
    ///     Order is load-bearing: <b>privilege is checked before the already-in-that-state
    ///     short-circuit.</b> The reverse makes the response depend on the row's current state — 204
    ///     for an already inactive school, 403 for an active one — which turns the status code into a
    ///     state oracle for an unprivileged caller. The cost is that an unprivileged deactivation of an
    ///     already inactive resource is a 403 rather than an idempotent 204; that is the correct trade
    ///     and it is asserted by a named test.
    ///     <para>
    ///         Both directions are guarded. DEC-20 speaks of deactivation, but reactivation restores a
    ///         school to accepting submissions and a code to the usable global namespace; treating the
    ///         directions differently would make the reactivating half of the same switch the
    ///         unguarded one.
    ///     </para>
    ///     <para>
    ///         A <c>false</c> return means the caller must <b>not</b> save. A no-op
    ///         <c>SaveChangesAsync</c> stamps <c>ModifiedAt</c> through the audit interceptor and
    ///         reports a change that did not happen, making <c>lastUpdatedAt</c> lie.
    ///     </para>
    /// </remarks>
    /// <param name="entity">The entity whose activation state is being set.</param>
    /// <param name="requestedIsActive">The state the request asks for.</param>
    /// <param name="currentUser">The acting identity.</param>
    /// <param name="privilege">The privilege class governing this entity's activation.</param>
    /// <param name="resourceName">Names the resource in the failure message, e.g. <c>School</c>.</param>
    /// <exception cref="ForbiddenException">The caller lacks the required privilege.</exception>
    public static bool Apply(
        IActivatable entity,
        bool requestedIsActive,
        ICurrentUser currentUser,
        ActivationPrivilege privilege,
        string resourceName)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(currentUser);

        if (privilege == ActivationPrivilege.SystemAdmin && !currentUser.IsSystemAdmin)
        {
            throw new ForbiddenException(
                ErrorCodes.System.Forbidden,
                $"{resourceName} activation state may only be changed by a system administrator.");
        }

        if (entity.IsActive == requestedIsActive)
            return false;

        entity.IsActive = requestedIsActive;

        return true;
    }

    /// <summary>
    ///     Applies the activation state carried by a full replace (<c>PUT</c>). Returns <c>true</c>
    ///     when the entity changed.
    /// </summary>
    /// <remarks>
    ///     The difference from <see cref="Apply" /> is what the request <em>means</em>, and it is the
    ///     one place the two verbs genuinely differ.
    ///     <list type="bullet">
    ///         <item>
    ///             <c>DELETE</c> <b>commands</b> a transition — "make this inactive" — so the caller is
    ///             exercising the privilege whatever the row currently says, and
    ///             <see cref="Apply" /> refuses before it looks at the row.
    ///         </item>
    ///         <item>
    ///             <c>PUT</c> <b>declares</b> a whole desired state. A body restating the activation
    ///             flag the row already carries is not an exercise of the privilege at all, and
    ///             refusing it would put the check on the endpoint rather than on the transition —
    ///             a non-admin could then not rename a school in their own scope, which is the failure
    ///             mode O-12 is about, with the sign flipped again.
    ///         </item>
    ///     </list>
    ///     <para>
    ///         The comparison lives here rather than in the update slice deliberately. A slice writing
    ///         its own <c>if (entity.IsActive != requested)</c> is a second home for the rule; a slice
    ///         calling this is not. Every activation change in the codebase still reaches
    ///         <see cref="Apply" /> or this method, so the privilege has exactly one home.
    ///     </para>
    ///     <para>
    ///         No state is disclosed by the relaxation: reaching either method means the caller has
    ///         already passed the scope check and can read the same flag from the resource's
    ///         <c>GET</c>.
    ///     </para>
    /// </remarks>
    /// <exception cref="ForbiddenException">The request asks for a transition the caller may not make.</exception>
    public static bool ApplyReplacement(
        IActivatable entity,
        bool requestedIsActive,
        ICurrentUser currentUser,
        ActivationPrivilege privilege,
        string resourceName)
    {
        // Both guards run before the state is consulted, so argument validation does not depend on
        // which branch the row happens to take.
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(currentUser);

        if (entity.IsActive == requestedIsActive)
            return false;

        return Apply(entity, requestedIsActive, currentUser, privilege, resourceName);
    }
}
