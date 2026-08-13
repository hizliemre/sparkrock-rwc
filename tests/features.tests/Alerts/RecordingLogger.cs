using Microsoft.Extensions.Logging;

namespace features.tests.Alerts;

/// <summary>
///     Records the <see cref="EventId" /> of every entry written.
/// </summary>
/// <remarks>
///     Conventions §6 bans mocking packages, so this is a hand-written double — the fifth copy, each
///     deliberately local to its aggregate so a shared <c>Fakes/</c> file is not a merge point for
///     every workstream.
///     <para>
///         <b>It is the only reliable discriminator this codebase has for a guard that returns
///         early.</b> Assigning a tracked entity a value it already holds leaves the change tracker
///         reporting no modification, so a <c>…_DoesNotWrite</c> assertion is satisfied by the
///         provider whether or not the handler checked anything — the defect has been found four
///         times (F02, F04, F05, F07). A handler that skipped the 409 and wrote the resolution anyway
///         announces a resolution; one that threw does not.
///     </para>
/// </remarks>
internal sealed class RecordingLogger<TCategoryName> : ILogger<TCategoryName>
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
