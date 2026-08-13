using System.Diagnostics;

namespace features.integration.tests;

/// <summary>
///     Covers the failure the whole tier depends on being legible: Docker is not there.
/// </summary>
/// <remarks>
///     Deliberately outside <see cref="IntegrationTestCollectionDefinition" /> — these need no
///     container, and making them wait for one would mean the check for "Docker is missing" could
///     only run when Docker is present.
///     <para>
///         The failure path is asserted through the endpoint-taking overload rather than by stopping
///         the daemon. A test that requires the reader to stop Docker is a test nobody runs, and
///         <c>DOCKER_HOST</c> cannot stand in for an outage: Testcontainers' resolver probes each
///         candidate and silently falls through to the working one.
///     </para>
/// </remarks>
public sealed class DockerAvailabilityTests
{
    /// <summary>The budget the tier promises: a missing daemon is reported in single-digit seconds.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task EnsureReachableAsync_WhenNoEndpointResolved_ThrowsWithoutProbing()
    {
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DockerAvailability.EnsureReachableAsync(endpoint: null));

        Assert.Contains("Docker is not available", exception.Message, StringComparison.Ordinal);
        Assert.Contains("no Docker endpoint could be detected", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureReachableAsync_WhenEndpointRefusesConnections_ThrowsWithinBudget()
    {
        // Port 2 is reserved and unbound, so the connect is refused rather than black-holed.
        Uri endpoint = new("tcp://127.0.0.1:2");

        Stopwatch stopwatch = Stopwatch.StartNew();

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DockerAvailability.EnsureReachableAsync(endpoint));

        stopwatch.Stop();

        Assert.Contains("Docker is not available", exception.Message, StringComparison.Ordinal);
        Assert.Contains("tcp://127.0.0.1:2", exception.Message, StringComparison.Ordinal);

        // The point of the preflight. Without it this is a socket timeout from inside container
        // startup, tens of seconds later, naming a PostgreSQL image instead of the actual problem.
        Assert.True(stopwatch.Elapsed < Budget, $"Took {stopwatch.Elapsed} — the failure must be fast.");
    }

    [Fact]
    public async Task EnsureReachableAsync_WhenSocketFileIsMissing_NamesTheSocketPath()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"sparkrock-absent-{Guid.NewGuid():N}.sock");
        Uri endpoint = new($"unix://{missing}");

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DockerAvailability.EnsureReachableAsync(endpoint));

        Assert.Contains(missing, exception.Message, StringComparison.Ordinal);
    }
}
