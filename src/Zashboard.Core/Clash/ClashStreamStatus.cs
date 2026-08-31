namespace Zashboard.Core.Clash;

public enum ClashStreamKind
{
    Connections,
    Logs,
    Traffic,
    Memory,
}

public enum ClashStreamState
{
    Connecting,
    Connected,
    Retrying,
    Unauthorized,
    Unsupported,
    Faulted,
}

public sealed record ClashStreamStatus(
    ClashStreamKind Kind,
    ClashStreamState State,
    int RetryAttempt,
    DateTimeOffset ChangedAt,
    int? HttpStatusCode = null,
    string? Detail = null);

public sealed class ClashStreamStatusChangedEventArgs : EventArgs
{
    public ClashStreamStatusChangedEventArgs(ClashStreamStatus status)
    {
        Status = status ?? throw new ArgumentNullException(nameof(status));
    }

    public ClashStreamStatus Status { get; }
}

public sealed class ClashStreamItemsDroppedEventArgs : EventArgs
{
    public ClashStreamItemsDroppedEventArgs(ClashStreamKind kind, long count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        Kind = kind;
        Count = count;
    }

    public ClashStreamKind Kind { get; }

    public long Count { get; }
}
