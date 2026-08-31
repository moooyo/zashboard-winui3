using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Zashboard.App.Controls;

public sealed record BackendDisplayItem(
    string Id,
    string Name,
    string Address,
    string Status,
    bool IsActive,
    bool HasStoredCredential)
{
    public string Id { get; set; } = Id;

    public string Name { get; set; } = Name;

    public string Address { get; set; } = Address;

    public string Status { get; set; } = Status;

    public bool IsActive { get; set; } = IsActive;

    public bool HasStoredCredential { get; set; } = HasStoredCredential;

    public string AccessibleDescription => $"{Name}. Status: {Status}. Address: {Address}.";
}

public sealed record ProxyGroupDisplayItem(
    string Name,
    string Type,
    string SelectedProxy,
    int NodeCount,
    string FixedProxy,
    bool HasFixedProxy,
    bool IsSmart)
{
    public string Name { get; set; } = Name;

    public string Type { get; set; } = Type;

    public string SelectedProxy { get; set; } = SelectedProxy;

    public int NodeCount { get; set; } = NodeCount;

    public string FixedProxy { get; set; } = FixedProxy;

    public bool HasFixedProxy { get; set; } = HasFixedProxy;

    public bool IsSmart { get; set; } = IsSmart;

    public string AccessibleDescription =>
        $"{Name}. Type: {Type}. {NodeCount} {(NodeCount == 1 ? "node" : "nodes")}. " +
        $"Current node: {SelectedProxy}.";
}

public sealed partial class ProxyNodeDisplayItem : ObservableObject
{
    private string _type;
    private string _latency;
    private string _availability;
    private string _provider;
    private string _rank;
    private string _weight;
    private bool _canTest;
    private bool _canSelect;
    private bool _supportsManualSelection;
    private bool _isCurrent;
    private bool _showSmartMetrics;
    private string _groupName = string.Empty;

    public ProxyNodeDisplayItem(
        string name,
        string type,
        string latency,
        string availability,
        string provider,
        string rank,
        string weight,
        bool canTest,
        bool canSelect,
        bool supportsManualSelection,
        bool isCurrent,
        bool showSmartMetrics)
    {
        Name = name;
        _type = type;
        _latency = latency;
        _availability = availability;
        _provider = provider;
        _rank = rank;
        _weight = weight;
        _canTest = canTest;
        _canSelect = canSelect;
        _supportsManualSelection = supportsManualSelection;
        _isCurrent = isCurrent;
        _showSmartMetrics = showSmartMetrics;
    }

    public string Name { get; }

    public string Type
    {
        get => _type;
        private set => SetProperty(ref _type, value);
    }

    public string Latency
    {
        get => _latency;
        private set => SetProperty(ref _latency, value);
    }

    public string Availability
    {
        get => _availability;
        private set => SetProperty(ref _availability, value);
    }

    public string Provider
    {
        get => _provider;
        private set => SetProperty(ref _provider, value);
    }

    public string Rank
    {
        get => _rank;
        private set => SetProperty(ref _rank, value);
    }

    public string Weight
    {
        get => _weight;
        private set => SetProperty(ref _weight, value);
    }

    public bool CanTest
    {
        get => _canTest;
        private set => SetProperty(ref _canTest, value);
    }

    public bool CanSelect
    {
        get => _canSelect;
        private set => SetProperty(ref _canSelect, value);
    }

    public bool SupportsManualSelection
    {
        get => _supportsManualSelection;
        private set => SetProperty(ref _supportsManualSelection, value);
    }

    public bool IsCurrent
    {
        get => _isCurrent;
        private set => SetProperty(ref _isCurrent, value);
    }

    public bool ShowSmartMetrics
    {
        get => _showSmartMetrics;
        private set => SetProperty(ref _showSmartMetrics, value);
    }

    public string GroupName
    {
        get => _groupName;
        private set => SetProperty(ref _groupName, value);
    }

    public bool IsSelectionUnavailable => !SupportsManualSelection;

    public string SelectionStatus => (IsCurrent, CanSelect, SupportsManualSelection) switch
    {
        (true, true, _) => "Current selection",
        (true, false, true) => "Current selection; selection is temporarily unavailable",
        (true, false, false) => "Active automatically; manual selection unavailable",
        (false, true, _) => "Available to select",
        (false, false, true) => "Selection is temporarily unavailable",
        _ => "Manual selection unavailable",
    };

    public string SelectionHelpText => CanSelect
        ? "Activate to make this the current node for the selected group."
        : SupportsManualSelection
            ? "Connect to a controller to change the current node."
            : "The selected group does not support manual node selection.";

    public string InteractionHelpText => CanTest
        ? $"{SelectionHelpText} Right-click to test latency."
        : SelectionHelpText;

    public string SelectActionName => string.IsNullOrWhiteSpace(GroupName)
        ? $"Select {Name}"
        : $"Select {Name} in {GroupName}";

    public string TestActionName => string.IsNullOrWhiteSpace(GroupName)
        ? $"Test latency for {Name}"
        : $"Test latency for {Name} in {GroupName}";

    public string AccessibleDescription =>
        $"{Name}. Type: {Type}. Status: {Availability}. Latency: {Latency}. " +
        $"Provider: {Provider}." +
        (ShowSmartMetrics ? $" Rank: {Rank}. Weight: {Weight}." : string.Empty) +
        $" {SelectionStatus}.";

    internal void ApplyProjectionContext(
        string groupName,
        string provider,
        string rank,
        string weight,
        bool canTest,
        bool canSelect,
        bool supportsManualSelection,
        bool isCurrent,
        bool showSmartMetrics)
    {
        DerivedState previous = CaptureDerivedState();
        GroupName = groupName;
        Provider = provider;
        Rank = rank;
        Weight = weight;
        CanTest = canTest;
        CanSelect = canSelect;
        SupportsManualSelection = supportsManualSelection;
        IsCurrent = isCurrent;
        ShowSmartMetrics = showSmartMetrics;
        RaiseDerivedChanges(previous);
    }

    public void UpdateFrom(ProxyNodeDisplayItem source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!string.Equals(Name, source.Name, StringComparison.Ordinal))
        {
            throw new ArgumentException("Proxy node names must match.", nameof(source));
        }

        DerivedState previous = CaptureDerivedState();
        Type = source.Type;
        Latency = source.Latency;
        Availability = source.Availability;
        Provider = source.Provider;
        Rank = source.Rank;
        Weight = source.Weight;
        CanTest = source.CanTest;
        CanSelect = source.CanSelect;
        SupportsManualSelection = source.SupportsManualSelection;
        IsCurrent = source.IsCurrent;
        ShowSmartMetrics = source.ShowSmartMetrics;
        GroupName = source.GroupName;
        RaiseDerivedChanges(previous);
    }

    private DerivedState CaptureDerivedState() => new(
        IsSelectionUnavailable,
        SelectionStatus,
        SelectionHelpText,
        InteractionHelpText,
        SelectActionName,
        TestActionName,
        AccessibleDescription);

    private void RaiseDerivedChanges(DerivedState previous)
    {
        DerivedState current = CaptureDerivedState();
        if (previous.IsSelectionUnavailable != current.IsSelectionUnavailable)
        {
            OnPropertyChanged(nameof(IsSelectionUnavailable));
        }

        RaiseIfChanged(previous.SelectionStatus, current.SelectionStatus, nameof(SelectionStatus));
        RaiseIfChanged(previous.SelectionHelpText, current.SelectionHelpText, nameof(SelectionHelpText));
        RaiseIfChanged(previous.InteractionHelpText, current.InteractionHelpText, nameof(InteractionHelpText));
        RaiseIfChanged(previous.SelectActionName, current.SelectActionName, nameof(SelectActionName));
        RaiseIfChanged(previous.TestActionName, current.TestActionName, nameof(TestActionName));
        RaiseIfChanged(
            previous.AccessibleDescription,
            current.AccessibleDescription,
            nameof(AccessibleDescription));
    }

    private void RaiseIfChanged(string previous, string current, string propertyName)
    {
        if (!string.Equals(previous, current, StringComparison.Ordinal))
        {
            OnPropertyChanged(propertyName);
        }
    }

    private readonly record struct DerivedState(
        bool IsSelectionUnavailable,
        string SelectionStatus,
        string SelectionHelpText,
        string InteractionHelpText,
        string SelectActionName,
        string TestActionName,
        string AccessibleDescription);
}

public sealed partial class ProxyGroupCardDisplayItem : ObservableObject
{
    private string _type;
    private string _selectedProxy;
    private int _nodeCount;
    private string _fixedProxy;
    private bool _hasFixedProxy;
    private bool _isSmart;
    private bool _canTest;
    private bool _canClearFixedProxy;
    private bool _canRefreshSmartWeights;
    private bool _canResetSmartWeights;
    private string _smartWeightsStatus;

    public ProxyGroupCardDisplayItem(
        string name,
        string type,
        string selectedProxy,
        int nodeCount,
        string fixedProxy,
        bool hasFixedProxy,
        bool isSmart,
        bool canTest,
        bool canClearFixedProxy,
        bool canRefreshSmartWeights,
        bool canResetSmartWeights,
        string smartWeightsStatus,
        IEnumerable<ProxyNodeDisplayItem> nodes)
    {
        Name = name;
        _type = type;
        _selectedProxy = selectedProxy;
        _nodeCount = nodeCount;
        _fixedProxy = fixedProxy;
        _hasFixedProxy = hasFixedProxy;
        _isSmart = isSmart;
        _canTest = canTest;
        _canClearFixedProxy = canClearFixedProxy;
        _canRefreshSmartWeights = canRefreshSmartWeights;
        _canResetSmartWeights = canResetSmartWeights;
        _smartWeightsStatus = smartWeightsStatus;
        Nodes = new ObservableCollection<ProxyNodeDisplayItem>(nodes);
    }

    public string Name { get; }

    public string Type
    {
        get => _type;
        private set => SetProperty(ref _type, value);
    }

    public string SelectedProxy
    {
        get => _selectedProxy;
        private set => SetProperty(ref _selectedProxy, value);
    }

    public int NodeCount
    {
        get => _nodeCount;
        private set => SetProperty(ref _nodeCount, value);
    }

    public string FixedProxy
    {
        get => _fixedProxy;
        private set => SetProperty(ref _fixedProxy, value);
    }

    public bool HasFixedProxy
    {
        get => _hasFixedProxy;
        private set => SetProperty(ref _hasFixedProxy, value);
    }

    public bool IsSmart
    {
        get => _isSmart;
        private set => SetProperty(ref _isSmart, value);
    }

    public bool CanTest
    {
        get => _canTest;
        private set => SetProperty(ref _canTest, value);
    }

    public bool CanClearFixedProxy
    {
        get => _canClearFixedProxy;
        private set => SetProperty(ref _canClearFixedProxy, value);
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

    public string SmartWeightsStatus
    {
        get => _smartWeightsStatus;
        private set => SetProperty(ref _smartWeightsStatus, value);
    }

    public ObservableCollection<ProxyNodeDisplayItem> Nodes { get; }

    public string AccessibleDescription =>
        $"{Name}. Type: {Type}. {NodeCount} {(NodeCount == 1 ? "node" : "nodes")}. " +
        $"Current node: {SelectedProxy}.";

    public string TestActionName => $"Test latency for group {Name}";

    public string ClearFixedActionName => $"Clear the fixed node for group {Name}";

    public string RefreshSmartActionName => $"Refresh Smart ranks and weights for group {Name}";

    public string ResetSmartActionName => $"Reset learned Smart weights for group {Name}";

    public void UpdateFrom(ProxyGroupCardDisplayItem source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!string.Equals(Name, source.Name, StringComparison.Ordinal))
        {
            throw new ArgumentException("Proxy group names must match.", nameof(source));
        }

        string previousDescription = AccessibleDescription;
        Type = source.Type;
        SelectedProxy = source.SelectedProxy;
        NodeCount = source.NodeCount;
        FixedProxy = source.FixedProxy;
        HasFixedProxy = source.HasFixedProxy;
        IsSmart = source.IsSmart;
        CanTest = source.CanTest;
        CanClearFixedProxy = source.CanClearFixedProxy;
        CanRefreshSmartWeights = source.CanRefreshSmartWeights;
        CanResetSmartWeights = source.CanResetSmartWeights;
        SmartWeightsStatus = source.SmartWeightsStatus;
        CollectionSynchronizer.ReconcileByKey(
            Nodes,
            source.Nodes.ToArray(),
            static node => node.Name,
            static (existing, updated) => existing.UpdateFrom(updated));
        if (!string.Equals(previousDescription, AccessibleDescription, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(AccessibleDescription));
        }
    }
}

public sealed record ProxyProviderDisplayItem(
    string Name,
    string VehicleType,
    int ProxyCount,
    string Usage,
    string Expires,
    string UpdatedAt,
    bool CanUpdate,
    bool CanCheck)
{
    public string Name { get; set; } = Name;

    public string VehicleType { get; set; } = VehicleType;

    public int ProxyCount { get; set; } = ProxyCount;

    public string Usage { get; set; } = Usage;

    public string Expires { get; set; } = Expires;

    public string UpdatedAt { get; set; } = UpdatedAt;

    public bool CanUpdate { get; set; } = CanUpdate;

    public bool CanCheck { get; set; } = CanCheck;

    public string UpdateActionName => $"Update provider {Name}";

    public string CheckActionName => $"Run health check for provider {Name}";

    public string AccessibleDescription =>
        $"{Name}. Type: {VehicleType}. {ProxyCount} " +
        $"{(ProxyCount == 1 ? "node" : "nodes")}. Used and total: {Usage}. " +
        $"Expires: {Expires}. Updated: {UpdatedAt}.";
}

public sealed partial class ConnectionDisplayItem : ObservableObject
{
    private string _host;
    private string _network;
    private string _rule;
    private string _chain;
    private string _download;
    private string _upload;
    private string _downloadTotal;
    private string _uploadTotal;
    private string _startedAt;
    private bool _canBlock;

    public ConnectionDisplayItem(
        string id,
        string host,
        string network,
        string rule,
        string chain,
        string download,
        string upload,
        string downloadTotal,
        string uploadTotal,
        string startedAt,
        bool canBlock)
    {
        Id = id;
        _host = host;
        _network = network;
        _rule = rule;
        _chain = chain;
        _download = download;
        _upload = upload;
        _downloadTotal = downloadTotal;
        _uploadTotal = uploadTotal;
        _startedAt = startedAt;
        _canBlock = canBlock;
    }

    public string Id { get; }

    public string Host
    {
        get => _host;
        private set => SetProperty(ref _host, value);
    }

    public string Network
    {
        get => _network;
        private set => SetProperty(ref _network, value);
    }

    public string Rule
    {
        get => _rule;
        private set => SetProperty(ref _rule, value);
    }

    public string Chain
    {
        get => _chain;
        private set => SetProperty(ref _chain, value);
    }

    public string Download
    {
        get => _download;
        private set => SetProperty(ref _download, value);
    }

    public string Upload
    {
        get => _upload;
        private set => SetProperty(ref _upload, value);
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

    public string StartedAt
    {
        get => _startedAt;
        private set => SetProperty(ref _startedAt, value);
    }

    public bool CanBlock
    {
        get => _canBlock;
        private set => SetProperty(ref _canBlock, value);
    }

    public string AccessibleDescription =>
        $"{Host}. Network: {Network}. Download rate: {Download}. Upload rate: {Upload}. " +
        $"Rule: {Rule}. Proxy chain: {Chain}.";

    public void UpdateFrom(ConnectionDisplayItem source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!string.Equals(Id, source.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException("Connection identifiers must match.", nameof(source));
        }

        string previousDescription = AccessibleDescription;
        Host = source.Host;
        Network = source.Network;
        Rule = source.Rule;
        Chain = source.Chain;
        Download = source.Download;
        Upload = source.Upload;
        DownloadTotal = source.DownloadTotal;
        UploadTotal = source.UploadTotal;
        StartedAt = source.StartedAt;
        CanBlock = source.CanBlock;
        if (!string.Equals(previousDescription, AccessibleDescription, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(AccessibleDescription));
        }
    }
}

public sealed partial class RecentConnectionDisplayItem : ObservableObject
{
    private string _host;
    private string _network;
    private string _rule;
    private string _download;
    private string _upload;

    public RecentConnectionDisplayItem(
        string id,
        string host,
        string network,
        string rule,
        string download,
        string upload)
    {
        Id = id;
        _host = host;
        _network = network;
        _rule = rule;
        _download = download;
        _upload = upload;
    }

    public string Id { get; }

    public string Host
    {
        get => _host;
        private set => SetProperty(ref _host, value);
    }

    public string Network
    {
        get => _network;
        private set => SetProperty(ref _network, value);
    }

    public string Rule
    {
        get => _rule;
        private set => SetProperty(ref _rule, value);
    }

    public string Download
    {
        get => _download;
        private set => SetProperty(ref _download, value);
    }

    public string Upload
    {
        get => _upload;
        private set => SetProperty(ref _upload, value);
    }

    public string AccessibleDescription =>
        $"{Host}. Network: {Network}. Download rate: {Download}. " +
        $"Upload rate: {Upload}. Rule: {Rule}.";

    public void UpdateFrom(RecentConnectionDisplayItem source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!string.Equals(Id, source.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException("Connection identifiers must match.", nameof(source));
        }

        string previousDescription = AccessibleDescription;
        Host = source.Host;
        Network = source.Network;
        Rule = source.Rule;
        Download = source.Download;
        Upload = source.Upload;
        if (!string.Equals(previousDescription, AccessibleDescription, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(AccessibleDescription));
        }
    }
}

public sealed record RuleDisplayItem(
    int Index,
    int? ApiIndex,
    string Type,
    string Payload,
    string Proxy,
    string Provider,
    string Identifier,
    bool Disabled,
    string State,
    string HitCount,
    string MissCount,
    bool CanToggle)
{
    public int Index { get; set; } = Index;

    public int? ApiIndex { get; set; } = ApiIndex;

    public string Type { get; set; } = Type;

    public string Payload { get; set; } = Payload;

    public string Proxy { get; set; } = Proxy;

    public string Provider { get; set; } = Provider;

    public string Identifier { get; set; } = Identifier;

    public bool Disabled { get; set; } = Disabled;

    public string State { get; set; } = State;

    public string HitCount { get; set; } = HitCount;

    public string MissCount { get; set; } = MissCount;

    public bool CanToggle { get; set; } = CanToggle;

    public bool Enabled => !Disabled;

    public string AccessibleDescription =>
        $"Rule {Index}. Type: {Type}. Payload: {Payload}. Target: {Proxy}. State: {State}.";

    public string ToggleAccessibleName => Enabled
        ? $"Disable rule {Index}, currently enabled"
        : $"Enable rule {Index}, currently disabled";
}

public sealed record RuleProviderDisplayItem(
    string Name,
    string Type,
    string Behavior,
    string Format,
    string RuleCount,
    string UpdatedAt,
    bool CanUpdate)
{
    public string Name { get; set; } = Name;

    public string Type { get; set; } = Type;

    public string Behavior { get; set; } = Behavior;

    public string Format { get; set; } = Format;

    public string RuleCount { get; set; } = RuleCount;

    public string UpdatedAt { get; set; } = UpdatedAt;

    public bool CanUpdate { get; set; } = CanUpdate;

    public string AccessibleDescription =>
        $"{Name}. Type: {Type}. Behavior: {Behavior}. Format: {Format}. " +
        $"Rules: {RuleCount}. Updated: {UpdatedAt}.";

    public string UpdateAccessibleName => $"Update {Name} rule provider";
}

public sealed record DnsAnswerDisplayItem(
    string Name,
    string Type,
    string Data,
    string Ttl)
{
    public string Name { get; set; } = Name;

    public string Type { get; set; } = Type;

    public string Data { get; set; } = Data;

    public string Ttl { get; set; } = Ttl;

    public string AccessibleDescription => $"{Name}. Type: {Type}. Data: {Data}. TTL: {Ttl}.";
}

public sealed record LogDisplayItem(
    long Sequence,
    DateTimeOffset Timestamp,
    string Level,
    string Message)
{
    public long Sequence { get; } = Sequence;

    public DateTimeOffset Timestamp { get; set; } = Timestamp;

    public string Level { get; set; } = Level;

    public string Message { get; set; } = Message;

    public string TimestampText => Timestamp.ToLocalTime().ToString(
        "HH:mm:ss.fff",
        System.Globalization.CultureInfo.CurrentCulture);

    public string AccessibleDescription => $"{TimestampText}. {Level}. {Message}";
}
