using Microsoft.Extensions.Logging;

namespace features.tests.Students;

/// <summary>
///     Records the <see cref="EventId" /> of every entry written, so a test can assert that a slice
///     which performed no write also claimed no write.
/// </summary>
/// <remarks>
///     Conventions §6 bans mocking packages, so this is a hand-written double. It lives beside the
///     Students tests rather than in <c>tests/features.tests/Fakes/</c> — the shared directory three
///     workstreams are editing concurrently — and nothing outside F05 uses it.
///     <para>
///         It exists because <c>Handle_WhenAlreadyInactive_DoesNotWrite</c> alone does not cover what
///         it appears to. EF's change tracker treats <c>student.IsActive = false</c> on an already
///         inactive row as no change, so a handler that skipped <c>ActivationPolicy</c> entirely and
///         assigned the flag directly still leaves <c>ModifiedAt</c> null and
///         <c>ChangeTracker.HasChanges()</c> false. The log line is the one observable difference:
///         that handler announces a deactivation that did not happen.
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
