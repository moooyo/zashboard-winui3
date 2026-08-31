using Zashboard.Core.Abstractions;
using Zashboard.Core.Backends;
using Zashboard.Core.Capabilities;
using Zashboard.Core.Clash;
using Zashboard.Infrastructure.Clash.Serialization;
using Zashboard.Infrastructure.Clash.Transport;
using Zashboard.Infrastructure.Clash.Wire;

namespace Zashboard.Infrastructure.Clash;

public sealed class ClashStreamClient : IClashStreamClient
{
    private readonly ClashWebSocketPump _pump;
    private readonly ClashWebSocketOptions _options;

    public ClashStreamClient(
        BackendProfile profile,
        BackendCredential credential,
        ICapabilityRegistry capabilities,
        CancellationToken sessionLifetime,
        ClashWebSocketOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _options = options ?? new ClashWebSocketOptions();
        _pump = new ClashWebSocketPump(
            profile.Endpoint,
            credential,
            capabilities,
            _options,
            PublishStatus,
            PublishItemsDropped,
            timeProvider,
            sessionLifetime);
    }

    public event EventHandler<ClashStreamStatusChangedEventArgs>? StreamStatusChanged;

    public event EventHandler<ClashStreamItemsDroppedEventArgs>? StreamItemsDropped;

    public IAsyncEnumerable<ConnectionStreamSnapshot> StreamConnectionsAsync(
        CancellationToken cancellationToken = default) =>
        _pump.StreamAsync(
            ClashStreamKind.Connections,
            ["connections"],
            null,
            ClashJsonContext.Default.ConnectionStreamSnapshotDto,
            ClashWireMapper.ToDomain,
            _options.ConnectionCapacity,
            null,
            cancellationToken);

    public IAsyncEnumerable<ClashLogMessage> StreamLogsAsync(
        ClashLogLevel minimumLevel,
        CancellationToken cancellationToken = default)
    {
        string level = ToWireLogLevel(minimumLevel);
        ClashCapability? capability = minimumLevel switch
        {
            ClashLogLevel.Trace => ClashCapability.TraceLogLevel,
            ClashLogLevel.Silent => ClashCapability.SilentLogLevel,
            _ => null,
        };

        return _pump.StreamAsync(
            ClashStreamKind.Logs,
            ["logs"],
            [ClashUriFactory.Query("level", level)],
            ClashJsonContext.Default.LogMessageDto,
            ClashWireMapper.ToDomain,
            _options.LogCapacity,
            capability,
            cancellationToken);
    }

    public IAsyncEnumerable<ClashTrafficSample> StreamTrafficAsync(
        CancellationToken cancellationToken = default) =>
        _pump.StreamAsync(
            ClashStreamKind.Traffic,
            ["traffic"],
            null,
            ClashJsonContext.Default.TrafficSampleDto,
            ClashWireMapper.ToDomain,
            _options.TrafficCapacity,
            null,
            cancellationToken);

    public IAsyncEnumerable<ClashMemorySample> StreamMemoryAsync(
        CancellationToken cancellationToken = default) =>
        _pump.StreamAsync(
            ClashStreamKind.Memory,
            ["memory"],
            null,
            ClashJsonContext.Default.MemorySampleDto,
            ClashWireMapper.ToDomain,
            _options.MemoryCapacity,
            null,
            cancellationToken);

    private void PublishStatus(ClashStreamStatus status) =>
        StreamStatusChanged?.Invoke(this, new ClashStreamStatusChangedEventArgs(status));

    private void PublishItemsDropped(ClashStreamItemsDroppedEventArgs args) =>
        StreamItemsDropped?.Invoke(this, args);

    private static string ToWireLogLevel(ClashLogLevel level) => level switch
    {
        ClashLogLevel.Unknown => "info",
        ClashLogLevel.Trace => "trace",
        ClashLogLevel.Debug => "debug",
        ClashLogLevel.Info => "info",
        ClashLogLevel.Warning => "warning",
        ClashLogLevel.Error => "error",
        ClashLogLevel.Fatal => "fatal",
        ClashLogLevel.Panic => "panic",
        ClashLogLevel.Silent => "silent",
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };
}
