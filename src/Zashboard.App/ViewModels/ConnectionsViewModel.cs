using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Zashboard.App.Controls;
using Zashboard.App.Services;
using Zashboard.Core.Backends;
using Zashboard.Core.Capabilities;
using Zashboard.Core.Clash;
using Zashboard.Core.Normalization;
using Zashboard.Core.Sessions;

namespace Zashboard.App.ViewModels;

public sealed partial class ConnectionsViewModel : ViewModelBase
{
    private string _query = string.Empty;
    private bool _isActive = true;
    private bool _isPaused;
    private ConnectionStreamSnapshot? _renderedSnapshot;
    private ConnectionSessionRenderState _renderedSessionState;

    public ConnectionsViewModel(AppSessionCoordinator coordinator)
        : base(coordinator)
    {
        RefreshCommand = new AsyncRelayCommand(ExecuteRefreshCommandAsync);
        DisconnectAllCommand = new AsyncRelayCommand(ExecuteDisconnectAllCommandAsync);
        DisconnectCommand = new AsyncRelayCommand<string>(ExecuteDisconnectCommandAsync);
        BlockCommand = new AsyncRelayCommand<string>(ExecuteBlockCommandAsync);
        SetQueryCommand = new AsyncRelayCommand<string>(ExecuteSetQueryCommandAsync);
        SetPausedCommand = new AsyncRelayCommand<bool>(ExecuteSetPausedCommandAsync);
        _renderedSnapshot = Coordinator.ConnectionSnapshot;
        RefreshConnections();
    }

    public ObservableCollection<ConnectionDisplayItem> Connections { get; } =
        new BulkObservableCollection<ConnectionDisplayItem>();

    public IAsyncRelayCommand RefreshCommand { get; }

    public IAsyncRelayCommand DisconnectAllCommand { get; }

    public IAsyncRelayCommand<string> DisconnectCommand { get; }

    public IAsyncRelayCommand<string> BlockCommand { get; }

    public IAsyncRelayCommand<string> SetQueryCommand { get; }

    public IAsyncRelayCommand<bool> SetPausedCommand { get; }

    public string Query
    {
        get => _query;
        private set => SetProperty(ref _query, value);
    }

    public bool IsPaused
    {
        get => _isPaused;
        private set => SetProperty(ref _isPaused, value);
    }

    public int TotalCount => _renderedSnapshot?.Connections.Count ?? 0;

    public void SetActive(bool isActive)
    {
        if (_isActive == isActive)
        {
            return;
        }

        _isActive = isActive;
        if (isActive)
        {
            if (!IsPaused)
            {
                CaptureLatestSnapshot();
            }

            RefreshConnections();
        }
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CaptureLatestSnapshot();
        IsPaused = false;
        RefreshConnections();
        return Task.CompletedTask;
    }

    public async Task DisconnectAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        if (!CanActOnConnection(connectionId))
        {
            return;
        }

        await ExecuteAsync(
            token => Coordinator.CloseConnectionAsync(connectionId, token),
            cancellationToken);
    }

    public async Task DisconnectAllAsync(CancellationToken cancellationToken = default)
    {
        if (!CanActOnRenderedSnapshot() || TotalCount == 0)
        {
            return;
        }

        await ExecuteAsync(Coordinator.CloseAllConnectionsAsync, cancellationToken);
    }

    public async Task BlockAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        if (!CanActOnConnection(connectionId))
        {
            return;
        }

        await ExecuteAsync(
            token => Coordinator.BlockSmartConnectionAsync(connectionId, token),
            cancellationToken);
    }

    public Task SetQueryAsync(string? query)
    {
        Query = query?.Trim() ?? string.Empty;
        RefreshConnections();
        return Task.CompletedTask;
    }

    public Task SetPausedAsync(bool isPaused)
    {
        if (IsPaused == isPaused)
        {
            return Task.CompletedTask;
        }

        CaptureLatestSnapshot();
        IsPaused = isPaused;
        RefreshConnections();
        return Task.CompletedTask;
    }

    protected override void HandleCoordinatorPropertyChanged(string? propertyName)
    {
        if (propertyName == nameof(AppSessionCoordinator.ConnectionSnapshot))
        {
            if (_isActive && !IsPaused)
            {
                CaptureLatestSnapshot();
                RefreshConnections();
            }

            return;
        }

        if (propertyName == nameof(AppSessionCoordinator.SessionSnapshot))
        {
            ConnectionSessionRenderState nextState = GetSessionRenderState();
            if (_renderedSessionState == nextState)
            {
                return;
            }

            bool epochChanged = _renderedSessionState.Epoch != nextState.Epoch;
            _renderedSessionState = nextState;
            if (epochChanged)
            {
                SetRenderedSnapshot(null);
                if (!_isActive)
                {
                    CollectionBatch.Replace(Connections, []);
                }
            }

            // The page also uses this notification to refresh controller action state.
            OnPropertyChanged(nameof(TotalCount));
            bool sessionUnavailable = Coordinator.SessionSnapshot.State is not
                BackendConnectionState.Online and not BackendConnectionState.Degraded;
            if (!IsPaused || sessionUnavailable || epochChanged)
            {
                RefreshConnections();
            }
        }
    }

    private Task ExecuteRefreshCommandAsync() => RefreshAsync();

    private Task ExecuteDisconnectAllCommandAsync() => DisconnectAllAsync();

    private Task ExecuteDisconnectCommandAsync(string? connectionId) =>
        string.IsNullOrWhiteSpace(connectionId)
            ? Task.CompletedTask
            : DisconnectAsync(connectionId);

    private Task ExecuteBlockCommandAsync(string? connectionId) =>
        string.IsNullOrWhiteSpace(connectionId)
            ? Task.CompletedTask
            : BlockAsync(connectionId);

    private Task ExecuteSetQueryCommandAsync(string? query) => SetQueryAsync(query);

    private Task ExecuteSetPausedCommandAsync(bool isPaused) => SetPausedAsync(isPaused);

    private void RefreshConnections()
    {
        if (!_isActive)
        {
            return;
        }

        _renderedSessionState = GetSessionRenderState();
        IEnumerable<ClashConnection> source = _renderedSnapshot?.Connections ?? [];
        if (Query.Length > 0)
        {
            source = source.Where(MatchesQuery);
        }

        CapabilitySupport blockSupport = GetCapabilitySupport(
            ClashCapability.SmartConnectionBlock);
        bool hasControlSession = Coordinator.HasActiveSession &&
            Coordinator.SessionSnapshot.State is
                BackendConnectionState.Online or BackendConnectionState.Degraded;
        ConnectionDisplayItem[] items = source
            .OrderByDescending(static connection => connection.StartedAt)
            .Select(connection =>
            {
                ConnectionTransferRate? rate = _renderedSnapshot?.TransferRates
                    .TryGetValue(connection.Id, out ConnectionTransferRate currentRate) == true
                    ? currentRate
                    : null;
                return DisplayModelFactory.Connection(
                    connection,
                    hasControlSession && blockSupport != CapabilitySupport.Unsupported &&
                    connection.Metadata.SmartBlock.Equals(
                        "normal",
                        StringComparison.OrdinalIgnoreCase),
                    rate);
            })
            .ToArray();
        CollectionSynchronizer.ReconcileByKey(
            Connections,
            items,
            static item => item.Id,
            static (current, updated) => current.UpdateFrom(updated));
    }

    private void CaptureLatestSnapshot()
    {
        SetRenderedSnapshot(Coordinator.ConnectionSnapshot);
    }

    private void SetRenderedSnapshot(ConnectionStreamSnapshot? snapshot)
    {
        int previousCount = TotalCount;
        _renderedSnapshot = snapshot;
        if (previousCount != TotalCount)
        {
            OnPropertyChanged(nameof(TotalCount));
        }
    }

    private bool CanActOnConnection(string connectionId) =>
        !string.IsNullOrWhiteSpace(connectionId) &&
        CanActOnRenderedSnapshot() &&
        _renderedSnapshot!.Connections.Any(connection => string.Equals(
            connection.Id,
            connectionId,
            StringComparison.Ordinal)) &&
        Coordinator.ConnectionSnapshot!.Connections.Any(connection => string.Equals(
            connection.Id,
            connectionId,
            StringComparison.Ordinal));

    private bool CanActOnRenderedSnapshot() =>
        _isActive &&
        !IsPaused &&
        Coordinator.HasActiveSession &&
        Coordinator.SessionSnapshot.State is
            BackendConnectionState.Online or BackendConnectionState.Degraded &&
        _renderedSessionState.Epoch == Coordinator.SessionSnapshot.Epoch &&
        ReferenceEquals(_renderedSnapshot, Coordinator.ConnectionSnapshot) &&
        _renderedSnapshot is not null;

    private CapabilitySupport GetCapabilitySupport(ClashCapability capability) =>
        Coordinator.SessionSnapshot.Capabilities.TryGetValue(
            capability,
            out CapabilityObservation? observation)
            ? observation.Support
            : CapabilitySupport.Unknown;

    private ConnectionSessionRenderState GetSessionRenderState() => new(
        Coordinator.SessionSnapshot.Epoch,
        Coordinator.SessionSnapshot.State,
        GetCapabilitySupport(ClashCapability.SmartConnectionBlock));

    private bool MatchesQuery(ClashConnection connection)
    {
        string query = Query;
        return ConnectionNormalizer.GetHost(connection).Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            connection.Metadata.Process.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            connection.Metadata.ProcessPath.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            connection.Rule.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            connection.RulePayload.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            connection.Chains.Any(chain => chain.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    private readonly record struct ConnectionSessionRenderState(
        SessionEpoch Epoch,
        BackendConnectionState State,
        CapabilitySupport SmartConnectionBlock);
}
