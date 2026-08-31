namespace Zashboard.Core.Backends;

public enum BackendConnectionState
{
    NoBackend,
    Connecting,
    Online,
    Degraded,
    Unauthorized,
    OfflineRetrying,
}

public enum BackendProbeFailureKind
{
    None,
    Unauthorized,
    Http,
    Timeout,
    Network,
    Protocol,
}

public static class BackendConnectionTransitions
{
    public static bool CanTransition(
        BackendConnectionState current,
        BackendConnectionState next)
    {
        if (current == next)
        {
            return true;
        }

        return current switch
        {
            BackendConnectionState.NoBackend => next == BackendConnectionState.Connecting,
            BackendConnectionState.Connecting => next is
                BackendConnectionState.NoBackend or
                BackendConnectionState.Online or
                BackendConnectionState.Degraded or
                BackendConnectionState.Unauthorized or
                BackendConnectionState.OfflineRetrying,
            BackendConnectionState.Online => next is
                BackendConnectionState.NoBackend or
                BackendConnectionState.Connecting or
                BackendConnectionState.Degraded or
                BackendConnectionState.Unauthorized or
                BackendConnectionState.OfflineRetrying,
            BackendConnectionState.Degraded => next is
                BackendConnectionState.NoBackend or
                BackendConnectionState.Connecting or
                BackendConnectionState.Online or
                BackendConnectionState.Unauthorized or
                BackendConnectionState.OfflineRetrying,
            BackendConnectionState.Unauthorized => next is
                BackendConnectionState.NoBackend or
                BackendConnectionState.Connecting,
            BackendConnectionState.OfflineRetrying => next is
                BackendConnectionState.NoBackend or
                BackendConnectionState.Connecting or
                BackendConnectionState.Online or
                BackendConnectionState.Degraded or
                BackendConnectionState.Unauthorized,
            _ => false,
        };
    }

    public static void EnsureCanTransition(
        BackendConnectionState current,
        BackendConnectionState next)
    {
        if (!CanTransition(current, next))
        {
            throw new InvalidOperationException($"Cannot transition a backend from {current} to {next}.");
        }
    }
}

public readonly record struct BackendProbeResult(
    bool IsSuccess,
    TimeSpan Latency,
    BackendProbeFailureKind FailureKind,
    string? Message = null)
{
    public static BackendProbeResult Success(TimeSpan latency) =>
        new(true, latency, BackendProbeFailureKind.None);

    public static BackendProbeResult Failure(
        TimeSpan latency,
        BackendProbeFailureKind kind,
        string? message = null)
    {
        if (kind == BackendProbeFailureKind.None)
        {
            throw new ArgumentException("A failed probe must include a failure kind.", nameof(kind));
        }

        return new BackendProbeResult(false, latency, kind, message);
    }
}
