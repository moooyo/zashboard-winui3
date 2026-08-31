using Zashboard.Core.Clash;

namespace Zashboard.Core.Abstractions;

public interface IClashStreamClient
{
    event EventHandler<ClashStreamStatusChangedEventArgs>? StreamStatusChanged;

    event EventHandler<ClashStreamItemsDroppedEventArgs>? StreamItemsDropped
    {
        add { }
        remove { }
    }

    IAsyncEnumerable<ConnectionStreamSnapshot> StreamConnectionsAsync(
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<ClashLogMessage> StreamLogsAsync(
        ClashLogLevel minimumLevel,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<ClashTrafficSample> StreamTrafficAsync(
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<ClashMemorySample> StreamMemoryAsync(
        CancellationToken cancellationToken = default);
}
