using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Zashboard.App.Controls;
using Zashboard.App.Services;
using Zashboard.Core.Backends;
using Zashboard.Core.Capabilities;
using Zashboard.Core.Clash;

namespace Zashboard.App.ViewModels;

public sealed record RuleQuery(string Text, string Filter);

public sealed record RuleToggleRequest(int? Index, string? Identifier, bool Disabled);

public sealed partial class RulesViewModel : ViewModelBase
{
    private string _query = string.Empty;
    private string _filter = "all";
    private int _totalRuleCount;
    private bool _canUpdateProviders;
    private BackendConnectionState _renderedSessionState;
    private CapabilitySupport _renderedIndexSupport;
    private CapabilitySupport _renderedIdentifierSupport;

    public RulesViewModel(AppSessionCoordinator coordinator)
        : base(coordinator)
    {
        RefreshCommand = new AsyncRelayCommand(ExecuteRefreshCommandAsync);
        SetQueryCommand = new AsyncRelayCommand<RuleQuery>(ExecuteSetQueryCommandAsync);
        ToggleRuleCommand = new AsyncRelayCommand<RuleToggleRequest>(ExecuteToggleRuleCommandAsync);
        UpdateProviderCommand = new AsyncRelayCommand<string>(ExecuteUpdateProviderCommandAsync);
        UpdateProvidersCommand = new AsyncRelayCommand(ExecuteUpdateProvidersCommandAsync);
        RefreshRules();
    }

    public ObservableCollection<RuleDisplayItem> Rules { get; } =
        new BulkObservableCollection<RuleDisplayItem>();

    public ObservableCollection<RuleProviderDisplayItem> Providers { get; } =
        new BulkObservableCollection<RuleProviderDisplayItem>();

    public IAsyncRelayCommand RefreshCommand { get; }

    public IAsyncRelayCommand<RuleQuery> SetQueryCommand { get; }

    public IAsyncRelayCommand<RuleToggleRequest> ToggleRuleCommand { get; }

    public IAsyncRelayCommand<string> UpdateProviderCommand { get; }

    public IAsyncRelayCommand UpdateProvidersCommand { get; }

    public string Query
    {
        get => _query;
        private set
        {
            if (SetProperty(ref _query, value))
            {
                OnPropertyChanged(nameof(HasActiveFilters));
                OnPropertyChanged(nameof(IsFilteredEmpty));
            }
        }
    }

    public string Filter
    {
        get => _filter;
        private set
        {
            if (SetProperty(ref _filter, value))
            {
                OnPropertyChanged(nameof(HasActiveFilters));
                OnPropertyChanged(nameof(IsFilteredEmpty));
            }
        }
    }

    public int TotalRuleCount
    {
        get => _totalRuleCount;
        private set
        {
            if (SetProperty(ref _totalRuleCount, value))
            {
                OnPropertyChanged(nameof(IsFilteredEmpty));
            }
        }
    }

    public bool HasActiveFilters => Query.Length > 0 || Filter != "all";

    public bool IsFilteredEmpty => IsFilteredEmptyState(
        TotalRuleCount,
        Rules.Count,
        Query,
        Filter);

    public bool CanUpdateProviders
    {
        get => _canUpdateProviders;
        private set => SetProperty(ref _canUpdateProviders, value);
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(token => Coordinator.RefreshRulesAsync(token), cancellationToken);

    public Task SetQueryAsync(string? query, string? filter)
    {
        Query = query?.Trim() ?? string.Empty;
        Filter = NormalizeFilter(filter);
        RefreshRules();
        return Task.CompletedTask;
    }

    public Task ToggleRuleAsync(
        int? index,
        string? identifier,
        bool disabled,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            token => Coordinator.SetRuleDisabledAsync(index, identifier, disabled, token),
            cancellationToken);

    public Task UpdateProviderAsync(
        string providerName,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            token => Coordinator.UpdateRuleProviderAsync(providerName, token),
            cancellationToken);

    protected override void HandleCoordinatorPropertyChanged(string? propertyName)
    {
        if (propertyName is nameof(AppSessionCoordinator.RuleCatalog) or
            nameof(AppSessionCoordinator.RuleProviders))
        {
            RefreshRules();
            return;
        }

        if (propertyName == nameof(AppSessionCoordinator.SessionSnapshot))
        {
            BackendConnectionState state = Coordinator.SessionSnapshot.State;
            CapabilitySupport indexSupport = GetCapabilitySupport(
                ClashCapability.RuleDisableByIndex);
            CapabilitySupport identifierSupport = GetCapabilitySupport(
                ClashCapability.RuleDisableByIdentifier);
            if (_renderedSessionState != state ||
                _renderedIndexSupport != indexSupport ||
                _renderedIdentifierSupport != identifierSupport)
            {
                RefreshRules();
            }
        }
    }

    private Task ExecuteRefreshCommandAsync() => RefreshAsync();

    private Task ExecuteSetQueryCommandAsync(RuleQuery? query) => query is null
        ? Task.CompletedTask
        : SetQueryAsync(query.Text, query.Filter);

    private Task ExecuteToggleRuleCommandAsync(RuleToggleRequest? request) => request is null
        ? Task.CompletedTask
        : ToggleRuleAsync(request.Index, request.Identifier, request.Disabled);

    private Task ExecuteUpdateProviderCommandAsync(string? providerName) =>
        string.IsNullOrWhiteSpace(providerName)
            ? Task.CompletedTask
            : UpdateProviderAsync(providerName);

    private Task ExecuteUpdateProvidersCommandAsync() =>
        ExecuteAsync(Coordinator.UpdateAllRuleProvidersAsync);

    private void RefreshRules()
    {
        IReadOnlyList<ClashRule> source = Coordinator.RuleCatalog?.Rules ?? [];
        BackendConnectionState sessionState = Coordinator.SessionSnapshot.State;
        CapabilitySupport indexSupport = GetCapabilitySupport(ClashCapability.RuleDisableByIndex);
        CapabilitySupport identifierSupport = GetCapabilitySupport(
            ClashCapability.RuleDisableByIdentifier);
        bool canChangeRules = Coordinator.HasActiveSession && sessionState is
            BackendConnectionState.Online or BackendConnectionState.Degraded;
        bool canDisableByIndex = canChangeRules && indexSupport != CapabilitySupport.Unsupported;
        bool canDisableByIdentifier = canChangeRules &&
            identifierSupport != CapabilitySupport.Unsupported;
        _renderedSessionState = sessionState;
        _renderedIndexSupport = indexSupport;
        _renderedIdentifierSupport = identifierSupport;
        TotalRuleCount = source.Count;
        List<RuleDisplayItem> items = new(source.Count);
        for (int index = 0; index < source.Count; index++)
        {
            ClashRule rule = source[index];
            if (MatchesFilter(rule) && MatchesQuery(rule))
            {
                items.Add(DisplayModelFactory.Rule(
                    rule,
                    index,
                    canDisableByIndex,
                    canDisableByIdentifier));
            }
        }

        ReplaceCollection(Rules, items);
        OnPropertyChanged(nameof(IsFilteredEmpty));
        CanUpdateProviders = Coordinator.HasActiveSession &&
            sessionState is
                BackendConnectionState.Online or BackendConnectionState.Degraded;
        ClashRuleProvider[] providers = Coordinator.RuleProviders?.Providers.Values
            .OrderBy(static provider => provider.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray() ?? [];
        ReplaceCollection(
            Providers,
            providers.Select(provider => DisplayModelFactory.RuleProvider(
                provider,
                CanUpdateProviders && !provider.VehicleType.Equals(
                    "Inline",
                    StringComparison.OrdinalIgnoreCase))));
    }

    private bool MatchesQuery(ClashRule rule) => Query.Length == 0 ||
        rule.Type.Contains(Query, StringComparison.CurrentCultureIgnoreCase) ||
        rule.Payload.Contains(Query, StringComparison.CurrentCultureIgnoreCase) ||
        rule.Proxy.Contains(Query, StringComparison.CurrentCultureIgnoreCase) ||
        (rule.Identifier?.Contains(Query, StringComparison.CurrentCultureIgnoreCase) ?? false);

    private bool MatchesFilter(ClashRule rule) => MatchesRuleTypeFilter(rule.Type, Filter);

    internal static bool MatchesRuleTypeFilter(string type, string filter) => filter switch
    {
        "all" => true,
        "domain" => type.Contains("DOMAIN", StringComparison.OrdinalIgnoreCase),
        "network" => ContainsRuleTypeToken(type, "IP") ||
            ContainsRuleTypeToken(type, "GEOIP") ||
            ContainsRuleTypeToken(type, "NETWORK"),
        "process" => type.Contains("PROCESS", StringComparison.OrdinalIgnoreCase),
        "final" => type.Equals("MATCH", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("FINAL", StringComparison.OrdinalIgnoreCase),
        _ => true,
    };

    internal static bool IsFilteredEmptyState(
        int totalRuleCount,
        int visibleRuleCount,
        string query,
        string filter) =>
        totalRuleCount > 0 &&
        visibleRuleCount == 0 &&
        (!string.IsNullOrWhiteSpace(query) || filter != "all");

    private static bool ContainsRuleTypeToken(string type, string expected)
    {
        ReadOnlySpan<char> remaining = type.AsSpan();
        while (!remaining.IsEmpty)
        {
            int separator = remaining.IndexOfAny('-', '_');
            ReadOnlySpan<char> token = separator < 0 ? remaining : remaining[..separator];
            if (token.Trim().Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (separator < 0)
            {
                break;
            }

            remaining = remaining[(separator + 1)..];
        }

        return false;
    }

    private static string NormalizeFilter(string? filter) => filter switch
    {
        "domain" => "domain",
        "network" => "network",
        "process" => "process",
        "final" => "final",
        _ => "all",
    };

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
