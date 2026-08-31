using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Zashboard.App.Controls;
using Zashboard.App.Services;
using Zashboard.Core.Capabilities;
using Zashboard.Core.Clash;

namespace Zashboard.App.ViewModels;

public sealed record ProxySelectionRequest(string GroupName, string ProxyName);

public sealed record ProxyTestRequest(
    string GroupName,
    string ProxyName,
    string? ProviderName);

public sealed partial class ProxiesViewModel : ViewModelBase
{
    private static readonly IReadOnlyDictionary<string, SmartNodeRank> EmptySmartRanks =
        new Dictionary<string, SmartNodeRank>(StringComparer.Ordinal);

    private readonly AppSettingsState _settings;

    private string? _selectedGroupName;
    private string _selectedGroupType = "--";
    private bool _canUpdateProviders;
    private bool _canCheckProviders;
    private bool _canClearFixedProxy;
    private bool _isSmartGroupSelected;
    private bool _canRefreshSmartWeights;
    private bool _canResetSmartWeights;
    private string _selectedFixedProxy = "--";
    private string _smartWeightsStatus = string.Empty;
    private ProxySessionRenderState _renderedSessionState;

    public ProxiesViewModel(
        AppSessionCoordinator coordinator,
        AppSettingsState settings)
        : base(coordinator)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        RefreshCommand = new AsyncRelayCommand(ExecuteRefreshCommandAsync);
        TestAllCommand = new AsyncRelayCommand(ExecuteTestAllCommandAsync);
        UpdateProvidersCommand = new AsyncRelayCommand(ExecuteUpdateProvidersCommandAsync);
        CheckProvidersCommand = new AsyncRelayCommand(ExecuteCheckProvidersCommandAsync);
        UpdateProviderCommand = new AsyncRelayCommand<string>(ExecuteUpdateProviderCommandAsync);
        CheckProviderCommand = new AsyncRelayCommand<string>(ExecuteCheckProviderCommandAsync);
        SelectGroupCommand = new AsyncRelayCommand<string>(ExecuteSelectGroupCommandAsync);
        TestGroupCommand = new AsyncRelayCommand<string>(ExecuteTestGroupCommandAsync);
        TestProxyCommand = new AsyncRelayCommand<ProxyTestRequest>(ExecuteTestProxyCommandAsync);
        SelectProxyCommand = new AsyncRelayCommand<ProxySelectionRequest>(ExecuteSelectProxyCommandAsync);
        ClearFixedProxyCommand = new AsyncRelayCommand(ExecuteClearFixedProxyCommandAsync);
        RefreshSmartWeightsCommand = new AsyncRelayCommand(ExecuteRefreshSmartWeightsCommandAsync);
        ResetSmartWeightsCommand = new AsyncRelayCommand(ExecuteResetSmartWeightsCommandAsync);
        _settings.PropertyChanged += OnSettingsPropertyChanged;
        RefreshFromCoordinator();
    }

    public ObservableCollection<ProxyGroupDisplayItem> ProxyGroups { get; } =
        new BulkObservableCollection<ProxyGroupDisplayItem>();

    public ObservableCollection<ProxyNodeDisplayItem> ProxyNodes { get; } =
        [];

    public ObservableCollection<ProxyGroupCardDisplayItem> ProxyGroupCards { get; } =
        [];

    public ObservableCollection<ProxyProviderDisplayItem> Providers { get; } =
        new BulkObservableCollection<ProxyProviderDisplayItem>();

    public IAsyncRelayCommand RefreshCommand { get; }

    public IAsyncRelayCommand TestAllCommand { get; }

    public IAsyncRelayCommand UpdateProvidersCommand { get; }

    public IAsyncRelayCommand CheckProvidersCommand { get; }

    public IAsyncRelayCommand<string> UpdateProviderCommand { get; }

    public IAsyncRelayCommand<string> CheckProviderCommand { get; }

    public IAsyncRelayCommand<string> SelectGroupCommand { get; }

    public IAsyncRelayCommand<string> TestGroupCommand { get; }

    public IAsyncRelayCommand<ProxyTestRequest> TestProxyCommand { get; }

    public IAsyncRelayCommand<ProxySelectionRequest> SelectProxyCommand { get; }

    public IAsyncRelayCommand ClearFixedProxyCommand { get; }

    public IAsyncRelayCommand RefreshSmartWeightsCommand { get; }

    public IAsyncRelayCommand ResetSmartWeightsCommand { get; }

    public string? SelectedGroupName
    {
        get => _selectedGroupName;
        private set
        {
            if (SetProperty(ref _selectedGroupName, value))
            {
                OnPropertyChanged(nameof(HasSelectedGroup));
            }
        }
    }

    public string SelectedGroupType
    {
        get => _selectedGroupType;
        private set => SetProperty(ref _selectedGroupType, value);
    }

    public bool HasSelectedGroup => !string.IsNullOrWhiteSpace(SelectedGroupName);

    public bool CanUpdateProviders
    {
        get => _canUpdateProviders;
        private set => SetProperty(ref _canUpdateProviders, value);
    }

    public bool CanCheckProviders
    {
        get => _canCheckProviders;
        private set => SetProperty(ref _canCheckProviders, value);
    }

    public bool CanClearFixedProxy
    {
        get => _canClearFixedProxy;
        private set => SetProperty(ref _canClearFixedProxy, value);
    }

    public bool IsSmartGroupSelected
    {
        get => _isSmartGroupSelected;
        private set => SetProperty(ref _isSmartGroupSelected, value);
    }

    public bool CanRefreshSmartWeights
    {
        get => _canRefreshSmartWeights;
        private set => SetProperty(ref _canRefreshSmartWeights, value);
    }

    public bool CanResetSmartWeights
    {
        get => _canResetSmartWeights;
        private set => SetProperty(ref _canResetSmartWeights, value);
    }

    public string SelectedFixedProxy
    {
        get => _selectedFixedProxy;
        private set => SetProperty(ref _selectedFixedProxy, value);
    }

    public string SmartWeightsStatus
    {
        get => _smartWeightsStatus;
        private set => SetProperty(ref _smartWeightsStatus, value);
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(token => Coordinator.RefreshAsync(token), cancellationToken);

    public Task SelectGroupAsync(string groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName))
        {
            SelectedGroupName = null;
            RefreshNodes();
            return Task.CompletedTask;
        }

        SelectedGroupName = groupName;
        RefreshNodes();
        return Task.CompletedTask;
    }

    public Task TestGroupAsync(
        string groupName,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            token => Coordinator.TestProxyGroupAsync(groupName, token),
            cancellationToken);

    public Task TestProxyAsync(
        string groupName,
        string proxyName,
        string? providerName,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            token => Coordinator.TestProxyAsync(
                groupName,
                proxyName,
                NormalizeProviderName(providerName),
                token),
            cancellationToken);

    public Task SelectProxyAsync(
        string groupName,
        string proxyName,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            token => Coordinator.SelectProxyAsync(groupName, proxyName, token),
            cancellationToken);

    public Task ClearFixedProxyAsync(CancellationToken cancellationToken = default) =>
        string.IsNullOrWhiteSpace(SelectedGroupName) || !CanClearFixedProxy
            ? Task.CompletedTask
            : ClearFixedProxyAsync(SelectedGroupName, cancellationToken);

    public Task ClearFixedProxyAsync(
        string groupName,
        CancellationToken cancellationToken = default) =>
        string.IsNullOrWhiteSpace(groupName) || !HasControlSession()
            ? Task.CompletedTask
            : ExecuteAsync(
                token => Coordinator.ClearFixedProxyAsync(groupName, token),
                cancellationToken);

    public Task RefreshSmartWeightsAsync(CancellationToken cancellationToken = default) =>
        !CanRefreshSmartWeights
            ? Task.CompletedTask
            : ExecuteAsync(Coordinator.RefreshSmartWeightsAsync, cancellationToken);

    public Task ResetSmartWeightsAsync(CancellationToken cancellationToken = default) =>
        !CanResetSmartWeights
            ? Task.CompletedTask
            : ExecuteAsync(Coordinator.ResetSmartWeightsAsync, cancellationToken);

    public Task UpdateProviderAsync(
        string providerName,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            token => Coordinator.UpdateProxyProviderAsync(providerName, token),
            cancellationToken);

    public Task CheckProviderAsync(
        string providerName,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            token => Coordinator.CheckProxyProviderAsync(providerName, token),
            cancellationToken);

    protected override void HandleCoordinatorPropertyChanged(string? propertyName)
    {
        if (propertyName is nameof(AppSessionCoordinator.ProxyCatalog) or
            nameof(AppSessionCoordinator.ProxyProviders) or
            nameof(AppSessionCoordinator.SmartWeights))
        {
            RefreshFromCoordinator();
            return;
        }

        if (propertyName == nameof(AppSessionCoordinator.SessionSnapshot) &&
            _renderedSessionState != GetSessionRenderState())
        {
            RefreshFromCoordinator();
        }
    }

    protected override void DisposeCore()
    {
        _settings.PropertyChanged -= OnSettingsPropertyChanged;
    }

    private Task ExecuteRefreshCommandAsync() => RefreshAsync();

    private Task ExecuteTestAllCommandAsync() =>
        ExecuteAsync(Coordinator.TestAllProxyGroupsAsync);

    private Task ExecuteUpdateProvidersCommandAsync() =>
        ExecuteAsync(Coordinator.UpdateAllProxyProvidersAsync);

    private Task ExecuteCheckProvidersCommandAsync() =>
        ExecuteAsync(Coordinator.CheckAllProxyProvidersAsync);

    private Task ExecuteUpdateProviderCommandAsync(string? providerName) =>
        string.IsNullOrWhiteSpace(providerName)
            ? Task.CompletedTask
            : UpdateProviderAsync(providerName);

    private Task ExecuteCheckProviderCommandAsync(string? providerName) =>
        string.IsNullOrWhiteSpace(providerName)
            ? Task.CompletedTask
            : CheckProviderAsync(providerName);

    private Task ExecuteSelectGroupCommandAsync(string? groupName) =>
        SelectGroupAsync(groupName ?? string.Empty);

    private Task ExecuteTestGroupCommandAsync(string? groupName) => string.IsNullOrWhiteSpace(groupName)
        ? Task.CompletedTask
        : TestGroupAsync(groupName);

    private Task ExecuteTestProxyCommandAsync(ProxyTestRequest? request) => request is null
        ? Task.CompletedTask
        : TestProxyAsync(request.GroupName, request.ProxyName, request.ProviderName);

    private Task ExecuteSelectProxyCommandAsync(ProxySelectionRequest? request) => request is null
        ? Task.CompletedTask
        : SelectProxyAsync(request.GroupName, request.ProxyName);

    private Task ExecuteClearFixedProxyCommandAsync() => ClearFixedProxyAsync();

    private Task ExecuteRefreshSmartWeightsCommandAsync() => RefreshSmartWeightsAsync();

    private Task ExecuteResetSmartWeightsCommandAsync() => ResetSmartWeightsAsync();

    private void RefreshFromCoordinator()
    {
        _renderedSessionState = GetSessionRenderState();
        ProxyCatalog catalog = Coordinator.ProxyCatalog ?? new ProxyCatalog();
        ClashProxy[] groups = catalog.Proxies.Values
            .Where(static proxy => proxy.All.Count > 0 && proxy.Hidden != true)
            .OrderBy(static proxy => proxy.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        ReplaceCollection(ProxyGroups, groups.Select(DisplayModelFactory.ProxyGroup));
        ClashProxyProvider[] providers = Coordinator.ProxyProviders?.Providers.Values
            .Where(static provider =>
                !provider.Name.Equals("default", StringComparison.OrdinalIgnoreCase) &&
                !provider.VehicleType.Equals("Compatible", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static provider => provider.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray() ?? [];
        CanUpdateProviders = HasControlSession() &&
            GetCapabilitySupport(ClashCapability.ProviderProxyUpdate) != CapabilitySupport.Unsupported;
        CanCheckProviders = HasControlSession() &&
            GetCapabilitySupport(ClashCapability.ProviderProxyHealthCheck) != CapabilitySupport.Unsupported;
        ReplaceCollection(
            Providers,
            providers.Select(provider => DisplayModelFactory.ProxyProvider(
                provider,
                CanUpdateProviders && !provider.VehicleType.Equals(
                    "Inline",
                    StringComparison.OrdinalIgnoreCase),
                CanCheckProviders)));
        if (SelectedGroupName is null ||
            !groups.Any(group => string.Equals(group.Name, SelectedGroupName, StringComparison.Ordinal)))
        {
            SelectedGroupName = groups.FirstOrDefault()?.Name;
        }

        ProxyProjectionContext context = CreateProjectionContext(catalog);
        ProxyGroupCardDisplayItem[] cards = groups
            .Select(group => CreateGroupCard(group, context))
            .ToArray();
        CollectionSynchronizer.ReconcileByKey(
            ProxyGroupCards,
            cards,
            static card => card.Name,
            static (existing, updated) => existing.UpdateFrom(updated));
        RefreshNodes();
    }

    private void RefreshNodes()
    {
        if (SelectedGroupName is null ||
            Coordinator.ProxyCatalog is not ProxyCatalog catalog ||
            !catalog.Proxies.TryGetValue(SelectedGroupName, out ClashProxy? group) ||
            group is null)
        {
            SelectedGroupType = "--";
            ProxyNodes.Clear();
            RefreshSelectedGroupState(null);
            return;
        }

        SelectedGroupType = string.IsNullOrWhiteSpace(group.Type) ? "--" : group.Type;
        ProxyGroupCardDisplayItem? card = ProxyGroupCards.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, group.Name, StringComparison.Ordinal));
        ProxyNodeDisplayItem[] nodes = card?.Nodes.ToArray() ?? [];
        CollectionSynchronizer.ReconcileByKey(
            ProxyNodes,
            nodes,
            static node => node.Name,
            static (existing, updated) => existing.UpdateFrom(updated));
        RefreshSelectedGroupState(group);
    }

    private List<ProxyNodeDisplayItem> BuildNodes(
        ClashProxy group,
        ProxyProjectionContext context)
    {
        bool isSmartGroup = IsSmartGroup(group);
        string? currentProxyName = string.IsNullOrWhiteSpace(group.Fixed)
            ? group.Now
            : group.Fixed;
        IReadOnlyDictionary<string, SmartNodeRank> ranks =
            isSmartGroup && context.SmartRanks.TryGetValue(
                group.Name,
                out Dictionary<string, SmartNodeRank>? groupRanks)
                ? groupRanks
                : EmptySmartRanks;
        bool supportsManualSelection = group.AllowsManualSelection;
        List<ProxyNodeDisplayItem> nodes = new(group.All.Count);
        foreach (string proxyName in group.All)
        {
            context.ProviderByProxy.TryGetValue(proxyName, out ProviderProxyEntry providerEntry);
            if (!context.Catalog.Proxies.TryGetValue(proxyName, out ClashProxy? proxy) &&
                providerEntry.Proxy is null)
            {
                continue;
            }

            proxy ??= providerEntry.Proxy!;
            string? providerName = NormalizeProviderName(proxy.ProviderName) ??
                providerEntry.ProviderName;
            Uri? providerTestUrl = providerName is not null &&
                context.ProviderTestUrls.TryGetValue(
                    providerName,
                    out Uri? resolvedProviderTestUrl)
                ? resolvedProviderTestUrl
                : null;
            Uri latencyTarget = group.TestUrl ??
                providerTestUrl ??
                proxy.TestUrl ??
                _settings.DelayTestUri;
            ProxyNodeDisplayItem item = DisplayModelFactory.ProxyNode(proxy, latencyTarget);
            ranks.TryGetValue(proxyName, out SmartNodeRank? rank);
            bool testableType = proxy.Kind is not
                ClashProxyKind.Reject and not
                ClashProxyKind.RejectDrop and not
                ClashProxyKind.Block;
            bool providerEndpointAvailable = string.IsNullOrWhiteSpace(providerName) ||
                context.ProviderDelaySupport != CapabilitySupport.Unsupported;
            item.ApplyProjectionContext(
                group.Name,
                string.IsNullOrWhiteSpace(providerName) ? item.Provider : providerName,
                !isSmartGroup || string.IsNullOrWhiteSpace(rank?.Rank)
                    ? "--"
                    : rank.Rank,
                !isSmartGroup || rank is null
                    ? "--"
                    : rank.Weight.ToString("0.###", CultureInfo.CurrentCulture),
                context.HasControlSession && testableType && providerEndpointAvailable,
                context.HasControlSession && supportsManualSelection,
                supportsManualSelection,
                string.Equals(
                    proxyName,
                    currentProxyName,
                    StringComparison.Ordinal),
                isSmartGroup);
            nodes.Add(item);
        }

        return nodes;
    }

    private ProxyGroupCardDisplayItem CreateGroupCard(
        ClashProxy group,
        ProxyProjectionContext context)
    {
        ProxyGroupDisplayItem summary = DisplayModelFactory.ProxyGroup(group);
        bool isSmart = IsSmartGroup(group);
        return new ProxyGroupCardDisplayItem(
            summary.Name,
            summary.Type,
            summary.SelectedProxy,
            summary.NodeCount,
            summary.FixedProxy,
            summary.HasFixedProxy,
            isSmart,
            context.HasControlSession,
            context.HasControlSession && summary.HasFixedProxy,
            context.HasControlSession && isSmart &&
                context.WeightsSupport != CapabilitySupport.Unsupported,
            context.HasControlSession && isSmart &&
                context.ResetSupport != CapabilitySupport.Unsupported,
            GetSmartWeightsStatus(isSmart, context.WeightsSupport),
            BuildNodes(group, context));
    }

    private ProxyProjectionContext CreateProjectionContext(ProxyCatalog catalog)
    {
        return new ProxyProjectionContext(
            catalog,
            BuildProviderLookup(),
            BuildProviderTestUrlLookup(),
            BuildSmartRankLookups(),
            HasControlSession(),
            GetCapabilitySupport(ClashCapability.ProviderProxyHealthCheck),
            GetCapabilitySupport(ClashCapability.SmartWeights),
            GetCapabilitySupport(ClashCapability.SmartWeightReset));
    }

    private Dictionary<string, ProviderProxyEntry> BuildProviderLookup()
    {
        Dictionary<string, ProviderProxyEntry> lookup = new(StringComparer.Ordinal);
        if (Coordinator.ProxyProviders is null)
        {
            return lookup;
        }

        foreach ((string providerName, ClashProxyProvider provider) in Coordinator.ProxyProviders.Providers)
        {
            if (provider.Name.Equals("default", StringComparison.OrdinalIgnoreCase) ||
                provider.VehicleType.Equals("Compatible", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (ClashProxy proxy in provider.Proxies)
            {
                lookup.TryAdd(
                    proxy.Name,
                    new ProviderProxyEntry(providerName, proxy));
            }
        }

        return lookup;
    }

    private Dictionary<string, Uri?> BuildProviderTestUrlLookup()
    {
        Dictionary<string, Uri?> lookup = new(StringComparer.Ordinal);
        if (Coordinator.ProxyProviders is null)
        {
            return lookup;
        }

        foreach ((string providerKey, ClashProxyProvider provider) in
            Coordinator.ProxyProviders.Providers)
        {
            lookup.TryAdd(providerKey, provider.TestUrl);
            lookup[provider.Name] = provider.TestUrl;
        }

        return lookup;
    }

    private Dictionary<string, Dictionary<string, SmartNodeRank>> BuildSmartRankLookups()
    {
        Dictionary<string, Dictionary<string, SmartNodeRank>> lookups =
            new(StringComparer.OrdinalIgnoreCase);
        if (Coordinator.SmartWeights is not SmartWeights weights)
        {
            return lookups;
        }

        foreach ((string groupName, IReadOnlyList<SmartNodeRank> groupRanks) in weights.Weights)
        {
            if (!lookups.TryGetValue(groupName, out Dictionary<string, SmartNodeRank>? lookup))
            {
                lookup = new Dictionary<string, SmartNodeRank>(StringComparer.Ordinal);
                lookups[groupName] = lookup;
            }

            foreach (SmartNodeRank rank in groupRanks)
            {
                if (!string.IsNullOrWhiteSpace(rank.Name))
                {
                    lookup[rank.Name] = rank;
                }
            }
        }

        return lookups;
    }

    private void RefreshSelectedGroupState(ClashProxy? group)
    {
        bool hasControlSession = HasControlSession();
        bool isSmart = group is not null && IsSmartGroup(group);
        CapabilitySupport weightsSupport = GetCapabilitySupport(ClashCapability.SmartWeights);
        CapabilitySupport resetSupport = GetCapabilitySupport(ClashCapability.SmartWeightReset);

        SelectedFixedProxy = string.IsNullOrWhiteSpace(group?.Fixed) ? "--" : group.Fixed;
        CanClearFixedProxy = hasControlSession && !string.IsNullOrWhiteSpace(group?.Fixed);
        IsSmartGroupSelected = isSmart;
        CanRefreshSmartWeights = hasControlSession && isSmart &&
            weightsSupport != CapabilitySupport.Unsupported;
        CanResetSmartWeights = hasControlSession && isSmart &&
            resetSupport != CapabilitySupport.Unsupported;
        SmartWeightsStatus = GetSmartWeightsStatus(isSmart, weightsSupport);
    }

    private string GetSmartWeightsStatus(bool isSmart, CapabilitySupport weightsSupport) =>
        !isSmart
            ? string.Empty
            : weightsSupport == CapabilitySupport.Unsupported
                ? "Smart weights are not supported by this backend."
                : Coordinator.SmartWeights is null
                    ? "Smart weight data is not available yet."
                    : string.IsNullOrWhiteSpace(Coordinator.SmartWeights.Message)
                        ? "Smart ranking is current."
                        : Coordinator.SmartWeights.Message;

    private static bool IsSmartGroup(ClashProxy group) =>
        group.Kind == ClashProxyKind.Smart ||
        group.Type.Equals("Smart", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeProviderName(string? providerName) =>
        string.IsNullOrWhiteSpace(providerName) || providerName == "--"
            ? null
            : providerName.Trim();

    private bool HasControlSession() => Coordinator.HasActiveSession &&
        Coordinator.SessionSnapshot.State is
            Zashboard.Core.Backends.BackendConnectionState.Online or
            Zashboard.Core.Backends.BackendConnectionState.Degraded;

    private CapabilitySupport GetCapabilitySupport(ClashCapability capability) =>
        Coordinator.SessionSnapshot.Capabilities.TryGetValue(
            capability,
            out CapabilityObservation? observation)
            ? observation.Support
            : CapabilitySupport.Unknown;

    private ProxySessionRenderState GetSessionRenderState() => new(
        Coordinator.SessionSnapshot.State,
        GetCapabilitySupport(ClashCapability.ProviderProxyUpdate),
        GetCapabilitySupport(ClashCapability.ProviderProxyHealthCheck),
        GetCapabilitySupport(ClashCapability.SmartWeights),
        GetCapabilitySupport(ClashCapability.SmartWeightReset));

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(AppSettingsState.DelayTestUrl))
        {
            RefreshFromCoordinator();
        }
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> destination, IEnumerable<T> source)
    {
        CollectionBatch.Replace(destination, source);
    }

    private readonly record struct ProviderProxyEntry(
        string? ProviderName,
        ClashProxy? Proxy);

    private readonly record struct ProxyProjectionContext(
        ProxyCatalog Catalog,
        Dictionary<string, ProviderProxyEntry> ProviderByProxy,
        Dictionary<string, Uri?> ProviderTestUrls,
        Dictionary<string, Dictionary<string, SmartNodeRank>> SmartRanks,
        bool HasControlSession,
        CapabilitySupport ProviderDelaySupport,
        CapabilitySupport WeightsSupport,
        CapabilitySupport ResetSupport);

    private readonly record struct ProxySessionRenderState(
        Zashboard.Core.Backends.BackendConnectionState State,
        CapabilitySupport ProviderUpdate,
        CapabilitySupport ProviderHealthCheck,
        CapabilitySupport SmartWeights,
        CapabilitySupport SmartWeightReset);
}
