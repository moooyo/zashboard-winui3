namespace Zashboard.Infrastructure.Clash;

public sealed class ClashWebSocketOptions
{
    public int ConnectionCapacity { get; init; } = 2;

    public int LogCapacity { get; init; } = 1024;

    public int TrafficCapacity { get; init; } = 16;

    public int MemoryCapacity { get; init; } = 16;

    public int ReceiveBufferSize { get; init; } = 16 * 1024;

    public int MaximumMessageSize { get; init; } = 16 * 1024 * 1024;

    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan MinimumReconnectDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan MaximumReconnectDelay { get; init; } = TimeSpan.FromSeconds(30);

    public int MaximumConsecutiveProtocolFailures { get; init; } = 3;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ConnectionCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(LogCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(TrafficCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MemoryCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(ReceiveBufferSize, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumMessageSize, ReceiveBufferSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumConsecutiveProtocolFailures, 1);

        if (KeepAliveInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(KeepAliveInterval));
        }

        if (MinimumReconnectDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumReconnectDelay));
        }

        if (MaximumReconnectDelay < MinimumReconnectDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumReconnectDelay));
        }
    }
}
