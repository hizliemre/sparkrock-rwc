using Microsoft.Extensions.Logging;

namespace features.tests.Schools;

/// <summary>
///     Records the <see cref="EventId" /> of every entry written, so a test can assert that a slice
///     which performed no write also claimed no write.
/// </summary>
/// <remarks>
///     <para>
///         Conventions §6 bans mocking packages, so this is a hand-written double. It exists because
///         <c>Handle_WhenAlreadyInactive_DoesNotWrite</c> alone does not cover what it appears to:
///         EF's change tracker treats <c>school.IsActive = false</c> on an already-inactive row as no
///         change, so a handler that skipped <see cref="domain.Security.ActivationPolicy" /> entirely
///         and assigned the flag directly still leaves <c>ModifiedAt</c> null and
///         <c>ChangeTracker.HasChanges()</c> false. The log line is the one observable difference —
///         that handler announces a deactivation that did not happen.
///     </para>
///     <para>
///         F04 found this weakness in its own copy of the test, fixed it, and reported that F02's was
///         identical and still inert. This is that fix. <b>The third copy of this class</b> — the
///         others are under <c>Students/</c> and <c>SchoolTerms/</c> — is deliberate for now: the
///         shared <c>Fakes/</c> directory has concurrent workstreams in it, and three small identical
///         doubles are cheaper than a merge conflict in a file every feature depends on. Consolidating
///         them is a follow-up.
///     </para>
/// </remarks>
internal sealed class RecordingLogger<TCategory> : ILogger<TCategory>
{
    private readonly List<int> _eventIds = [];

    public IReadOnlyList<int> EventIds => _eventIds;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _eventIds.Add(eventId.Id);
    }
}
