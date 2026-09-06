using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Zashboard.App.Controls;
using Zashboard.App.Services;
using Zashboard.Core.Backends;
using Zashboard.Core.Capabilities;
using Zashboard.Core.Clash;
using Zashboard.Core.Sessions;

namespace Zashboard.App.ViewModels;

internal readonly record struct TrafficHistoryPoint(long Download, long Upload);

internal readonly record struct MemoryHistoryPoint(long InUse);

public sealed partial class OverviewViewModel : ViewModelBase
{
    private const int TrafficHistoryCapacity = 300;
    private const int MemoryHistoryCapacity = 120;

    private static readonly string[] DefaultModes = ["direct", "rule", "global"];

    private bool _isActive = true;
    private string _sessionState = "No backend";
    private string _mode = "--";
    private string _connectionCount = "--";
    private string _memory = "--";
    private string _coreVersion = "--";
    private string _downloadRate = "--";
    private string _uploadRate = "--";
    private string _downloadTotal = "Total --";
    private string _uploadTotal = "Total --";
    private bool _isOnline;
    private string? _selectedMode;
    private bool _isTunEnabled;
    private bool _canChangeMode;
    private bool _canChangeTun;
    private bool _isHonkStatisticsVisible;
    private bool _canRefreshHonkStatistics;
    private string _honkStatisticsStatus = string.Empty;
    private string _honkTotalConnections = "--";
    private string _honkActiveConnections = "--";
    private string _honkUpload = "--";
    private string _honkDownload = "--";
    private string _honkErrors = "--";
    private string _sessionStatusDetail = "Choose a backend to load controller data.";
    private readonly List<TrafficHistoryPoint> _trafficHistory = [];
    private readonly List<MemoryHistoryPoint> _memoryHistory = [];
    private SessionEpoch _telemetryEpoch;
    private DateTimeOffset _lastMemoryHistoryAt;
    private long _trafficRevision;
    private long _memoryRevision;

    public OverviewViewModel(AppSessionCoordinator coordinator)
        : base(coordinator)
    {
        _telemetryEpoch = coordinator.Epoch;
        RefreshCommand = new AsyncRelayCommand(ExecuteRefreshCommandAsync);
        SetModeCommand = new AsyncRelayCommand<string>(ExecuteSetModeCommandAsync);
        SetTunEnabledCommand = new AsyncRelayCommand<bool>(ExecuteSetTunEnabledCommandAsync);
        RefreshHonkStatisticsCommand = new AsyncRelayCommand(
            ExecuteRefreshHonkStatisticsCommandAsync);
        RefreshFromCoordinator();
    }

    public ObservableCollection<RecentConnectionDisplayItem> RecentConnections { get; } = [];

    public ObservableCollection<string> Modes { get; } = new BulkObservableCollection<string>();

    public IAsyncRelayCommand RefreshCommand { get; }

    public IAsyncRelayCommand<string> SetModeCommand { get; }

    public IAsyncRelayCommand<bool> SetTunEnabledCommand { get; }

    public IAsyncRelayCommand RefreshHonkStatisticsCommand { get; }

    public string SessionState
    {
        get => _sessionState;
        private set => SetProperty(ref _sessionState, value);
    }

    public string Mode
    {
        get => _mode;
        private set => SetProperty(ref _mode, value);
    }

    public string ConnectionCount
    {
        get => _connectionCount;
        private set => SetProperty(ref _connectionCount, value);
    }

    public string Memory
    {
        get => _memory;
        private set => SetProperty(ref _memory, value);
    }

    public string CoreVersion
    {
        get => _coreVersion;
        private set => SetProperty(ref _coreVersion, value);
    }

    public string DownloadRate
    {
        get => _downloadRate;
        private set => SetProperty(ref _downloadRate, value);
    }

    public string UploadRate
    {
        get => _uploadRate;
        private set => SetProperty(ref _uploadRate, value);
    }

    public string DownloadTotal
    {
        get => _downloadTotal;
        private set => SetProperty(ref _downloadTotal, value);
    }

    public string UploadTotal
    {
        get => _uploadTotal;
        private set => SetProperty(ref _uploadTotal, value);
    }

    public long TrafficRevision
    {
        get => _trafficRevision;
        private set => SetProperty(ref _trafficRevision, value);
    }

    public long MemoryRevision
    {
        get => _memoryRevision;
        private set => SetProperty(ref _memoryRevision, value);
    }

    internal IReadOnlyList<TrafficHistoryPoint> TrafficHistory => _trafficHistory;

    internal IReadOnlyList<MemoryHistoryPoint> MemoryHistory => _memoryHistory;

    public bool IsOnline
    {
        get => _isOnline;
        private set => SetProperty(ref _isOnline, value);
    }

    public string SessionStatusDetail
    {
        get => _sessionStatusDetail;
        private set => SetProperty(ref _sessionStatusDetail, value);
    }

    public string? SelectedMode
    {
        get => _selectedMode;
        private set => SetProperty(ref _selectedMode, value);
    }

    public bool IsTunEnabled
    {
        get => _isTunEnabled;
        private set => SetProperty(ref _isTunEnabled, value);
    }

    public bool CanChangeMode
    {
        get => _canChangeMode;
        private set => SetProperty(ref _canChangeMode, value);
    }

    public bool CanChangeTun
    {
        get => _canChangeTun;
        private set => SetProperty(ref _canChangeTun, value);
    }

    public bool IsHonkStatisticsVisible
    {
        get => _isHonkStatisticsVisible;
        private set => SetProperty(ref _isHonkStatisticsVisible, value);
    }

    public bool CanRefreshHonkStatistics
    {
        get => _canRefreshHonkStatistics;
        private set => SetProperty(ref _canRefreshHonkStatistics, value);
    }

    public string HonkStatisticsStatus
    {
        get => _honkStatisticsStatus;
        private set => SetProperty(ref _honkStatisticsStatus, value);
    }

    public string HonkTotalConnections
    {
        get => _honkTotalConnections;
        private set => SetProperty(ref _honkTotalConnections, value);
    }

    public string HonkActiveConnections
    {
        get => _honkActiveConnections;
        private set => SetProperty(ref _honkActiveConnections, value);
    }

    public string HonkUpload
    {
        get => _honkUpload;
        private set => SetProperty(ref _honkUpload, value);
    }

    public string HonkDownload
    {
        get => _honkDownload;
        private set => SetProperty(ref _honkDownload, value);
    }

    public string HonkErrors
    {
        get => _honkErrors;
        private set => SetProperty(ref _honkErrors, value);
    }

    public void SetActive(bool isActive)
    {
        if (_isActive == isActive)
        {
            return;
        }

        _isActive = isActive;
        if (isActive)
        {
            RefreshRecentConnections(Coordinator.ConnectionSnapshot);
        }
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(token => Coordinator.RefreshAsync(token), cancellationToken);

    public Task SetModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        if (!CanChangeMode || string.IsNullOrWhiteSpace(mode) ||
            string.Equals(mode, SelectedMode, StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        return ExecuteAsync(token => Coordinator.SetModeAsync(mode, token), cancellationToken);
    }

    public Task SetTunEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (!CanChangeTun || enabled == IsTunEnabled)
        {
            return Task.CompletedTask;
        }

        return ExecuteAsync(token => Coordinator.SetTunEnabledAsync(enabled, token), cancellationToken);
    }

    public Task RefreshHonkStatisticsAsync(CancellationToken cancellationToken = default) =>
        !CanRefreshHonkStatistics
            ? Task.CompletedTask
            : ExecuteAsync(Coordinator.RefreshHonkRuntimeStatisticsAsync, cancellationToken);

    protected override void HandleCoordinatorPropertyChanged(string? propertyName)
    {
        switch (propertyName)
        {
            case nameof(AppSessionCoordinator.SessionSnapshot):
                RefreshSessionState();
                break;
            case nameof(AppSessionCoordinator.Configuration):
                RefreshConfiguration();
                break;
            case nameof(AppSessionCoordinator.ConnectionSnapshot):
                RefreshConnections();
                break;
            case nameof(AppSessionCoordinator.TrafficSample):
                RefreshTraffic();
                break;
            case nameof(AppSessionCoordinator.MemorySample):
                RefreshMemory();
                break;
            case nameof(AppSessionCoordinator.HonkRuntimeStatistics):
                RefreshHonkStatistics(HasControlSession());
                break;
            case nameof(AppSessionCoordinator.ActiveProfile):
                RefreshControlAvailability();
                break;
        }
    }

    private Task ExecuteRefreshCommandAsync() => RefreshAsync();

    private Task ExecuteSetModeCommandAsync(string? mode) =>
        string.IsNullOrWhiteSpace(mode) ? Task.CompletedTask : SetModeAsync(mode);

    private Task ExecuteSetTunEnabledCommandAsync(bool enabled) => SetTunEnabledAsync(enabled);

    private Task ExecuteRefreshHonkStatisticsCommandAsync() => RefreshHonkStatisticsAsync();

    private void RefreshFromCoordinator()
    {
        RefreshSessionState();
        RefreshConfiguration();
        RefreshConnections();
        RefreshTraffic();
        RefreshMemory();
        RefreshHonkStatistics(HasControlSession());
    }

    private void RefreshSessionState()
    {
        EnsureTelemetryEpoch();
        BackendConnectionState state = Coordinator.SessionSnapshot.State;
        SessionState = DisplayModelFactory.FormatState(state);
        IsOnline = state == BackendConnectionState.Online;
        SessionStatusDetail = GetSessionStatusDetail();
        CoreVersion = string.IsNullOrWhiteSpace(Coordinator.SessionSnapshot.Version)
            ? "--"
            : Coordinator.SessionSnapshot.Version;
        RefreshControlAvailability();
        RefreshHonkStatistics(HasControlSession());
    }

    private void RefreshConfiguration()
    {
        Mode = string.IsNullOrWhiteSpace(Coordinator.Configuration?.Mode)
            ? "--"
            : Coordinator.Configuration.Mode;
        ClashConfiguration? configuration = Coordinator.Configuration;
        List<string> advertisedModes = configuration is null
            ? []
            : configuration.ModeList
                .Concat(configuration.Modes)
                .Where(static mode => !string.IsNullOrWhiteSpace(mode))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        if (configuration is not null && advertisedModes.Count == 0)
        {
            advertisedModes.AddRange(DefaultModes);
        }

        string[] modes = configuration is null
            ? []
            : advertisedModes
                .Append(configuration.Mode)
                .Where(static mode => !string.IsNullOrWhiteSpace(mode))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        ReplaceCollection(Modes, modes);
        SelectedMode = modes.FirstOrDefault(mode => string.Equals(
            mode,
            configuration?.Mode,
            StringComparison.OrdinalIgnoreCase)) ?? modes.FirstOrDefault();
        IsTunEnabled = configuration?.Tun?.Enabled == true;
        RefreshControlAvailability();
    }

    private void RefreshControlAvailability()
    {
        bool hasControlSession = HasControlSession();
        CapabilitySupport patchSupport = GetCapabilitySupport(ClashCapability.ConfigurationPatch);
        CanChangeMode = hasControlSession && Modes.Count > 0 &&
            patchSupport != CapabilitySupport.Unsupported;
        CanChangeTun = hasControlSession && Coordinator.Configuration?.Tun is not null &&
            Coordinator.ActiveProfile?.DisableTunMode != true &&
            patchSupport != CapabilitySupport.Unsupported;
    }

    private void RefreshConnections()
    {
        ConnectionStreamSnapshot? connections = Coordinator.ConnectionSnapshot;
        ConnectionCount = connections is null
            ? "--"
            : connections.Connections.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
        if (Coordinator.MemorySample is null && connections is not null)
        {
            RefreshMemory();
        }

        RefreshTransferTotals();

        // Keep telemetry history current while deferring the offscreen row projection.
        if (_isActive || connections is null)
        {
            RefreshRecentConnections(connections);
        }
    }

    private void RefreshRecentConnections(ConnectionStreamSnapshot? connections)
    {
        IReadOnlyDictionary<string, ConnectionTransferRate> transferRates =
            connections?.TransferRates ??
            new Dictionary<string, ConnectionTransferRate>(StringComparer.Ordinal);
        RecentConnectionDisplayItem[] recent = connections?.Connections
            .OrderByDescending(static connection => connection.StartedAt)
            .Take(12)
            .Select(connection =>
            {
                ConnectionTransferRate? rate = transferRates.TryGetValue(
                    connection.Id,
                    out ConnectionTransferRate currentRate)
                    ? currentRate
                    : null;
                return DisplayModelFactory.RecentConnection(connection, rate);
            })
            .ToArray() ?? [];
        CollectionSynchronizer.ReconcileByKey(
            RecentConnections,
            recent,
            static item => item.Id,
            static (current, updated) => current.UpdateFrom(updated));
    }

    private void RefreshTraffic()
    {
        EnsureTelemetryEpoch();
        DownloadRate = Coordinator.TrafficSample is null
            ? "--"
            : DisplayModelFactory.FormatBytes(Coordinator.TrafficSample.Down, perSecond: true);
        UploadRate = Coordinator.TrafficSample is null
            ? "--"
            : DisplayModelFactory.FormatBytes(Coordinator.TrafficSample.Up, perSecond: true);
        RefreshTransferTotals();
        if (Coordinator.TrafficSample is ClashTrafficSample sample)
        {
            AppendBounded(
                _trafficHistory,
                new TrafficHistoryPoint(Math.Max(0, sample.Down), Math.Max(0, sample.Up)),
                TrafficHistoryCapacity);
        }

        TrafficRevision = NextRevision(TrafficRevision);
    }

    private void RefreshMemory()
    {
        EnsureTelemetryEpoch();
        long? memory = Coordinator.MemorySample?.InUse is > 0 and var streamedMemory
            ? streamedMemory
            : null;
        if (!memory.HasValue && Coordinator.ConnectionSnapshot?.Memory is > 0 and var snapshotMemory)
        {
            memory = snapshotMemory;
        }

        Memory = memory.HasValue ? DisplayModelFactory.FormatBytes(memory.Value) : "--";
        if (memory.HasValue)
        {
            MemoryHistoryPoint point = new(Math.Max(0, memory.Value));
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (_memoryHistory.Count == 0 ||
                now - _lastMemoryHistoryAt >= TimeSpan.FromMilliseconds(500))
            {
                AppendBounded(_memoryHistory, point, MemoryHistoryCapacity);
                _lastMemoryHistoryAt = now;
            }
            else
            {
                _memoryHistory[^1] = point;
            }
        }
        else
        {
            _memoryHistory.Clear();
            _lastMemoryHistoryAt = default;
        }

        MemoryRevision = NextRevision(MemoryRevision);
    }

    private void RefreshTransferTotals()
    {
        long? download = Coordinator.TrafficSample?.DownTotal ??
            Coordinator.ConnectionSnapshot?.DownloadTotal;
        long? upload = Coordinator.TrafficSample?.UpTotal ??
            Coordinator.ConnectionSnapshot?.UploadTotal;
        DownloadTotal = download.HasValue
            ? $"Total {DisplayModelFactory.FormatBytes(download.Value)}"
            : "Total --";
        UploadTotal = upload.HasValue
            ? $"Total {DisplayModelFactory.FormatBytes(upload.Value)}"
            : "Total --";
    }

    private bool HasControlSession() => Coordinator.HasActiveSession &&
        Coordinator.SessionSnapshot.State is
            BackendConnectionState.Online or BackendConnectionState.Degraded;

    private void EnsureTelemetryEpoch()
    {
        if (_telemetryEpoch == Coordinator.Epoch)
        {
            return;
        }

        _telemetryEpoch = Coordinator.Epoch;
        _trafficHistory.Clear();
        _memoryHistory.Clear();
        _lastMemoryHistoryAt = default;
        TrafficRevision = NextRevision(TrafficRevision);
        MemoryRevision = NextRevision(MemoryRevision);
    }

    private static void AppendBounded<T>(List<T> destination, T item, int capacity)
    {
        if (destination.Count == capacity)
        {
            destination.RemoveAt(0);
        }

        destination.Add(item);
    }

    private static long NextRevision(long current) => current == long.MaxValue ? 0 : current + 1;

    private string GetSessionStatusDetail()
    {
        if (!string.IsNullOrWhiteSpace(Coordinator.SessionSnapshot.StatusDetail))
        {
            return Coordinator.SessionSnapshot.StatusDetail;
        }

        return Coordinator.SessionSnapshot.State switch
        {
            BackendConnectionState.NoBackend => "Choose a backend to load controller data.",
            BackendConnectionState.Connecting => "Connecting to the selected controller.",
            BackendConnectionState.Degraded => "Some controller data is currently unavailable.",
            BackendConnectionState.Unauthorized => "Update the saved secret for this controller.",
            BackendConnectionState.OfflineRetrying => "The controller is unavailable; reconnection is in progress.",
            _ => string.Empty,
        };
    }

    private void RefreshHonkStatistics(bool hasControlSession)
    {
        CapabilitySupport support = GetCapabilitySupport(ClashCapability.RuntimeStatistics);
        bool hasRuntimeStatistics = Coordinator.HonkRuntimeStatistics is not null;
        bool isCandidate = Coordinator.SessionSnapshot.CoreKind == ClashCoreKind.Honk ||
            hasRuntimeStatistics || support == CapabilitySupport.Supported;
        IsHonkStatisticsVisible = isCandidate;
        CanRefreshHonkStatistics = isCandidate && hasControlSession &&
            support != CapabilitySupport.Unsupported;
        HonkStatisticsStatus = !isCandidate
            ? string.Empty
            : support == CapabilitySupport.Unsupported
                ? "Runtime statistics are not supported by this backend."
                : Coordinator.HonkRuntimeStatistics is null
                    ? "Waiting for the first runtime statistics sample..."
                    : $"{Coordinator.HonkRuntimeStatistics.Outbounds.Count.ToString(CultureInfo.CurrentCulture)} outbound(s); refreshed automatically.";

        IReadOnlyList<HonkOutboundStatistics>? outbounds = Coordinator.HonkRuntimeStatistics?.Outbounds;
        if (outbounds is null)
        {
            HonkTotalConnections = "--";
            HonkActiveConnections = "--";
            HonkUpload = "--";
            HonkDownload = "--";
            HonkErrors = "--";
            return;
        }

        HonkTotalConnections = SumSaturated(outbounds, static item => item.TotalConnections)
            .ToString("N0", CultureInfo.CurrentCulture);
        HonkActiveConnections = SumSaturated(outbounds, static item => item.ActiveConnections)
            .ToString("N0", CultureInfo.CurrentCulture);
        HonkUpload = DisplayModelFactory.FormatBytes(
            SumSaturated(outbounds, static item => item.Upload));
        HonkDownload = DisplayModelFactory.FormatBytes(
            SumSaturated(outbounds, static item => item.Download));
        HonkErrors = SumSaturated(outbounds, static item => item.Errors)
            .ToString("N0", CultureInfo.CurrentCulture);
    }

    private static long SumSaturated(
        IEnumerable<HonkOutboundStatistics> values,
        Func<HonkOutboundStatistics, long> selector)
    {
        long total = 0;
        foreach (HonkOutboundStatistics value in values)
        {
            long next = Math.Max(0, selector(value));
            if (total > long.MaxValue - next)
            {
                return long.MaxValue;
            }

            total += next;
        }

        return total;
    }

    private CapabilitySupport GetCapabilitySupport(ClashCapability capability) =>
        Coordinator.SessionSnapshot.Capabilities.TryGetValue(
            capability,
            out CapabilityObservation? observation)
            ? observation.Support
            : CapabilitySupport.Unknown;

    private static void ReplaceCollection<T>(ObservableCollection<T> destination, IEnumerable<T> source)
    {
        CollectionBatch.Replace(destination, source);
    }
}
