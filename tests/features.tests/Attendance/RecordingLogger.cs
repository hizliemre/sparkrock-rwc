using Microsoft.Extensions.Logging;

namespace features.tests.Attendance;

/// <summary>
///     Records the <see cref="EventId" /> of every entry written.
/// </summary>
/// <remarks>
///     Conventions §6 bans mocking packages, so this is a hand-written double. The fourth copy — the
///     others are under <c>Schools/</c>, <c>Students/</c> and <c>SchoolTerms/</c>, each deliberately
///     local so a shared <c>Fakes/</c> file is not a merge point for every workstream. Consolidating
///     them is a follow-up.
///     <para>
///         F07 needs it for something the others do not: the retry loop logs <b>inside</b> the loop
///         (EventId 1501), which is the only observable evidence at the handler tier that an attempt
///         was discarded rather than never made. O-40 records that DEC-14's bound cannot be tuned
///         without a counter and there is no metrics pipeline, so the log line is the substitute.
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
