using domain.Security;

namespace api;

/// <summary>
///     Placeholder identity used until authentication exists.
/// </summary>
/// <remarks>
///     A system administrator with no school scope, which means every authorisation check passes.
///     That is why the deployment guard exists, and why test doubles must default to a non-admin —
///     otherwise a handler that omits its scoping entirely goes undetected.
///     <para>
///         This records strictly less than the legacy system did, which wrote a per-login database
///         user. Accepted with risk, and the reason the audit regression is logged rather than
///         presented as neutral plumbing.
///     </para>
///     <para>
///         It lives in <c>api</c> rather than a shared library so that the day authentication lands
///         it is one deleted file and one changed registration — and so no library can accidentally
///         depend on an anonymous identity being available.
///     </para>
/// </remarks>
internal sealed class StubCurrentUser : ICurrentUser
{
    public Guid UserId => new("00000000-0000-0000-0000-00000000000A");

    public string DisplayName => "Stub User";

    public IReadOnlyCollection<Guid> AuthorizedSchoolIds => [];

    public bool IsSystemAdmin => true;
}
