using Microsoft.Extensions.Logging;

namespace features.tests.SchoolTerms;

/// <summary>
///     An <see cref="ILogger{TCategoryName}" /> that records the event ids written to it.
/// </summary>
/// <remarks>
///     Conventions §6 puts test doubles in <c>tests/features.tests/Fakes/</c>; this one lives beside
///     the tests that use it because F04's change set is scoped to <c>SchoolTerms/</c>. Move it when
///     a second aggregate needs it.
///     <para>
///         It exists because the "already inactive, so do not write" branch is otherwise
///         <b>unobservable</b>. Assigning <c>IsActive = false</c> to a row that is already inactive
///         leaves EF's change tracker empty, so an unguarded <c>SaveChangesAsync</c> writes nothing
///         and stamps nothing — a test asserting on <c>ModifiedAt</c> or <c>HasChanges</c> passes
///         whether or not the guard exists. The log line is the one effect that does differ:
///         reporting a deactivation that did not happen is wrong, and it is the assertion that makes
///         the guard's absence visible.
///     </para>
/// </remarks>
internal sealed class RecordingLogger<TCategoryName> : ILogger<TCategoryName>
{
    private readonly List<EventId> _events = [];

    public IReadOnlyList<EventId> Events => _events;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) => _events.Add(eventId);
}
