using System.Globalization;
using System.IO.Pipes;
using System.Net.Sockets;
using DotNet.Testcontainers.Configurations;

namespace features.integration.tests;

/// <summary>
///     Preflight check run before the container starts: is a Docker daemon actually reachable?
/// </summary>
/// <remarks>
///     Docker is a hard prerequisite for this tier and there is deliberately no fallback provider — a
///     suite that quietly degrades to EF InMemory reports green while verifying none of the relational
///     behaviour it exists for.
///     <para>
///         Without this probe the absent-daemon failure surfaces tens of seconds later, from inside
///         container startup, as a socket error naming a PostgreSQL image. That sends the reader after
///         the wrong thing. Five seconds and a sentence naming the endpoint is the whole point.
///     </para>
///     <para>
///         The endpoint comes from Testcontainers' own resolution rather than a guess: it honours
///         <c>DOCKER_HOST</c>, the active Docker context and <c>~/.testcontainers.properties</c>.
///         Guessing <c>/var/run/docker.sock</c> is wrong on the most common developer machine — Docker
///         Desktop on macOS listens on <c>~/.docker/run/docker.sock</c>.
///     </para>
///     <para>
///         Reachability is probed with a plain socket connect rather than a Docker API ping. It needs
///         no dependency beyond the BCL, and the failure it has to catch — daemon stopped, socket gone
///         or refusing — is a connect failure.
///     </para>
///     <para>
///         Testcontainers' resolver already tries each candidate endpoint and skips unavailable ones,
///         so on a healthy machine this probe agrees with it and costs milliseconds. What it adds is
///         the other case: when nothing is available the resolver still hands back its last-resort
///         candidate, and the failure would otherwise surface much later and much less clearly.
///     </para>
/// </remarks>
internal static class DockerAvailability
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    public static Task EnsureAvailableAsync(CancellationToken cancellationToken = default)
    {
        IDockerEndpointAuthenticationConfiguration? authConfig = TestcontainersSettings.OS.DockerEndpointAuthConfig;

        return EnsureReachableAsync(authConfig?.Endpoint, cancellationToken);
    }

    /// <summary>
    ///     The half that takes an endpoint rather than resolving one, so the failure path is testable
    ///     without stopping the reader's Docker daemon.
    /// </summary>
    internal static async Task EnsureReachableAsync(Uri? endpoint, CancellationToken cancellationToken = default)
    {
        if (endpoint is null)
            throw Unavailable(endpoint: null, reason: "no Docker endpoint could be detected");

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            await ProbeAsync(endpoint, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Unavailable(
                endpoint,
                $"the endpoint did not accept a connection within {ProbeTimeout.TotalSeconds:0} seconds");
        }
        catch (Exception exception) when (exception is SocketException or IOException or TimeoutException or UnauthorizedAccessException)
        {
            throw Unavailable(endpoint, exception.Message, exception);
        }
    }

    private static async Task ProbeAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        switch (endpoint.Scheme)
        {
            case "unix":
                await ProbeUnixSocketAsync(endpoint.LocalPath, cancellationToken);
                return;

            case "npipe":
                await ProbeNamedPipeAsync(endpoint, cancellationToken);
                return;

            default:
                await ProbeTcpAsync(endpoint, cancellationToken);
                return;
        }
    }

    private static async Task ProbeUnixSocketAsync(string socketPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(socketPath))
            throw new FileNotFoundException($"The Docker socket '{socketPath}' does not exist.", socketPath);

        using Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
    }

    private static async Task ProbeNamedPipeAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        // npipe://./pipe/docker_engine -> "docker_engine"
        string pipeName = endpoint.AbsolutePath.TrimStart('/');
        const string PipePrefix = "pipe/";

        if (pipeName.StartsWith(PipePrefix, StringComparison.Ordinal))
            pipeName = pipeName[PipePrefix.Length..];

        using NamedPipeClientStream pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(cancellationToken);
    }

    private static async Task ProbeTcpAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        using TcpClient client = new();
        await client.ConnectAsync(endpoint.Host, endpoint.Port, cancellationToken);
    }

    private static InvalidOperationException Unavailable(Uri? endpoint, string reason, Exception? inner = null)
    {
        string where = endpoint is null
            ? "No endpoint was resolved"
            : string.Create(CultureInfo.InvariantCulture, $"Probed '{endpoint}'");

        string message =
            $"Docker is not available, so the integration test tier cannot run. {where} and {reason}. "
            + "This tier runs PostgreSQL in a Testcontainers container and has no local-server fallback "
            + "by design. Start Docker (Docker Desktop, Colima, or the daemon on Linux), or point "
            + "DOCKER_HOST at a reachable endpoint, then re-run. The handler tier in "
            + "tests/features.tests needs no Docker and can be run in the meantime.";

        return inner is null ? new InvalidOperationException(message) : new InvalidOperationException(message, inner);
    }
}
