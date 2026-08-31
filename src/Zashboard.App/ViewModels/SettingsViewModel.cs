using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.ApplicationModel;
using Zashboard.App.Controls;
using Zashboard.App.Services;
using Zashboard.Core.Backends;
using Zashboard.Core.Capabilities;
using Zashboard.Core.Clash;
using Zashboard.Core.Sessions;

namespace Zashboard.App.ViewModels;

public sealed record SettingUpdate(string Key, object Value);

public sealed record NetworkConfigurationUpdate(
    int HttpPort,
    int SocksPort,
    int RedirPort,
    int TProxyPort,
    int MixedPort,
    bool AllowLan);

public sealed partial class SettingsViewModel : ViewModelBase
{
    private const string StartupTaskId = "ZashboardStartup";
    private const string ListenerSettingsUpdatedMessage =
        "Controller listener settings were updated.";

    private readonly AppSettingsState _settings;
    private readonly ListenerSettingsDraftState _listenerSettingsDraft = new();
    private string _dnsSummary = "No DNS query has been run.";
    private string? _operationMessage;
    private bool _hasControlSession;
    private bool _canReloadConfiguration;
    private bool _canUpdateConfiguration;
    private bool _canUpdateGeoData;
    private bool _canRestartCore;
    private bool _canUpgradeCore;
    private bool _canPatchConfiguration;
    private bool _hasConfiguration;
    private SessionEpoch? _dnsResultEpoch;

    public SettingsViewModel(
        AppSessionCoordinator coordinator,
        AppSettingsState settings)
        : base(coordinator)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        UpdateSettingCommand = new AsyncRelayCommand<SettingUpdate>(ExecuteUpdateSettingCommandAsync);
        ClearCapabilityCacheCommand = new AsyncRelayCommand(ExecuteClearCapabilityCacheCommandAsync);
        _settings.PropertyChanged += OnSettingsPropertyChanged;
        AppVersion = GetPackageVersion();
        RefreshControlAvailability();
    }

    public ObservableCollection<DnsAnswerDisplayItem> DnsAnswers { get; } =
        new BulkObservableCollection<DnsAnswerDisplayItem>();

    public IAsyncRelayCommand<SettingUpdate> UpdateSettingCommand { get; }

    public IAsyncRelayCommand ClearCapabilityCacheCommand { get; }

    public string AppVersion { get; }

    public bool StartWithWindows => _settings.StartWithWindows;

    public string CloseBehavior => _settings.CloseBehavior;

    public string Theme => _settings.Theme;

    public bool MicaBackdrop => _settings.MicaBackdrop;

    public int LogBufferSize => _settings.LogBufferSize;

    public string DelayTestUrl => _settings.DelayTestUrl;

    public int DelayTimeoutSeconds => _settings.DelayTimeoutMilliseconds / 1_000;

    public int HttpPort => Coordinator.Configuration?.Port ?? 0;

    public int SocksPort => Coordinator.Configuration?.SocksPort ?? 0;

    public int RedirPort => Coordinator.Configuration?.RedirPort ?? 0;

    public int TProxyPort => Coordinator.Configuration?.TProxyPort ?? 0;

    public int MixedPort => Coordinator.Configuration?.MixedPort ?? 0;

    public bool AllowLan => Coordinator.Configuration?.AllowLan == true;

    internal bool HasListenerSettingsDraft => _listenerSettingsDraft.IsDirty;

    public long ControllerSessionRevision => Coordinator.Epoch.Value;

    internal ListenerSettingsDraft GetListenerSettingsForDisplay() =>
        _listenerSettingsDraft.Resolve(
            ControllerSessionRevision,
            new ListenerSettingsDraft(
                HttpPort,
                SocksPort,
                RedirPort,
                TProxyPort,
                MixedPort,
                AllowLan));

    internal void UpdateListenerSettingsDraft(ListenerSettingsDraft draft) =>
        _listenerSettingsDraft.MarkEdited(ControllerSessionRevision, draft);

    internal void MarkListenerSettingsSaved() => _listenerSettingsDraft.MarkSaved();

    public string ControllerConfigurationStatus =>
        SettingsFormPolicy.DescribeControllerConfiguration(HasControlSession);

    public string ListenerSettingsStatus => SettingsFormPolicy.DescribeListenerSettings(
        HasControlSession,
        HasConfiguration,
        CanPatchConfiguration);

    public bool HasConfiguration
    {
        get => _hasConfiguration;
        private set => SetProperty(ref _hasConfiguration, value);
    }

    public bool CanPatchConfiguration
    {
        get => _canPatchConfiguration;
        private set => SetProperty(ref _canPatchConfiguration, value);
    }

    public string DnsSummary
    {
        get => _dnsSummary;
        private set => SetProperty(ref _dnsSummary, value);
    }

    public string? OperationMessage
    {
        get => _operationMessage;
        private set => SetProperty(ref _operationMessage, value);
    }

    public bool HasControlSession
    {
        get => _hasControlSession;
        private set => SetProperty(ref _hasControlSession, value);
    }

    public bool CanReloadConfiguration
    {
        get => _canReloadConfiguration;
        private set => SetProperty(ref _canReloadConfiguration, value);
    }

    public bool CanUpdateConfiguration
    {
        get => _canUpdateConfiguration;
        private set => SetProperty(ref _canUpdateConfiguration, value);
    }

    public bool CanUpdateGeoData
    {
        get => _canUpdateGeoData;
        private set => SetProperty(ref _canUpdateGeoData, value);
    }

    public bool CanRestartCore
    {
        get => _canRestartCore;
        private set => SetProperty(ref _canRestartCore, value);
    }

    public bool CanUpgradeCore
    {
        get => _canUpgradeCore;
        private set => SetProperty(ref _canUpgradeCore, value);
    }

    public Task UpdateSettingAsync(string key, object value)
    {
        if (key == "startWithWindows" && value is bool enabled)
        {
            return ExecuteAsync(_ => UpdateStartupTaskAsync(enabled));
        }

        try
        {
            ClearError();
            _settings.Apply(key, value);
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }

        return Task.CompletedTask;
    }

    public Task ClearCapabilityCacheAsync() => ReprobeCapabilitiesAsync();

    public Task RefreshStartupTaskStateAsync() => ExecuteAsync(
        async _ =>
        {
            try
            {
                StartupTask startupTask = await StartupTask.GetAsync(StartupTaskId);
                _settings.StartWithWindows = startupTask.State is
                    StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Startup task state could not be read: {exception.Message}");
            }
        });

    public Task ReprobeCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        RunControlOperationAsync(
            Coordinator.ReprobeCapabilitiesAsync,
            "Capability observations were refreshed.",
            cancellationToken);

    public async Task<bool> ApplyNetworkConfigurationAsync(
        NetworkConfigurationUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await RunControlOperationAsync(
            token => Coordinator.PatchConfigurationAsync(
                new ClashConfigurationPatch
                {
                    Port = update.HttpPort,
                    SocksPort = update.SocksPort,
                    RedirPort = update.RedirPort,
                    TProxyPort = update.TProxyPort,
                    MixedPort = update.MixedPort,
                    AllowLan = update.AllowLan,
                },
                token),
            ListenerSettingsUpdatedMessage,
            cancellationToken);
        return string.Equals(
            OperationMessage,
            ListenerSettingsUpdatedMessage,
            StringComparison.Ordinal);
    }

    public Task ReloadConfigurationAsync(CancellationToken cancellationToken = default) =>
        RunControlOperationAsync(
            Coordinator.ReloadConfigurationAsync,
            "The controller configuration was reloaded.",
            cancellationToken);

    public Task UpdateConfigurationAsync(
        string? path,
        string? payload,
        bool force,
        CancellationToken cancellationToken = default) =>
        RunControlOperationAsync(
            token => Coordinator.UpdateConfigurationAsync(path, payload, force, token),
            "The controller configuration was updated.",
            cancellationToken);

    public Task QueryDnsAsync(
        string name,
        string type,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(async token =>
        {
            OperationMessage = null;
            DnsQueryResult result = await Coordinator.QueryDnsAsync(name, type, token);
            CollectionBatch.Replace(DnsAnswers, result.Answers.Select(DisplayModelFactory.DnsAnswer));
            DnsSummary = $"Status {result.Status}; {result.Answers.Count} answer(s).";
            _dnsResultEpoch = Coordinator.Epoch;
        }, cancellationToken);

    public Task FlushDnsCacheAsync(CancellationToken cancellationToken = default) =>
        RunControlOperationAsync(
            Coordinator.FlushDnsCacheAsync,
            "The DNS cache was flushed.",
            cancellationToken);

    public Task FlushFakeIpCacheAsync(CancellationToken cancellationToken = default) =>
        RunControlOperationAsync(
            Coordinator.FlushFakeIpCacheAsync,
            "The FakeIP cache was flushed.",
            cancellationToken);

    public Task UpdateGeoDataAsync(CancellationToken cancellationToken = default) =>
        RunControlOperationAsync(
            Coordinator.UpdateGeoDataAsync,
            "Geo data was updated.",
            cancellationToken);

    public Task RestartCoreAsync(CancellationToken cancellationToken = default) =>
        RunControlOperationAsync(
            Coordinator.RestartCoreAsync,
            "The restart request was accepted and the session was reconnected.",
            cancellationToken);

    public Task UpgradeCoreAsync(
        string? channel,
        CancellationToken cancellationToken = default)
    {
        CoreUpgradeChannel parsed = channel switch
        {
            "release" => CoreUpgradeChannel.Release,
            "alpha" => CoreUpgradeChannel.Alpha,
            _ => CoreUpgradeChannel.Auto,
        };
        return RunControlOperationAsync(
            token => Coordinator.UpgradeCoreAsync(parsed, token),
            "The upgrade request was accepted and the session was reconnected.",
            cancellationToken);
    }

    protected override void HandleCoordinatorPropertyChanged(string? propertyName)
    {
        if (propertyName is nameof(AppSessionCoordinator.SessionSnapshot) or
            nameof(AppSessionCoordinator.ActiveProfile))
        {
            OnPropertyChanged(nameof(ControllerSessionRevision));
            if (_dnsResultEpoch.HasValue && _dnsResultEpoch.Value != Coordinator.Epoch)
            {
                _dnsResultEpoch = null;
                DnsAnswers.Clear();
                DnsSummary = "No DNS query has been run.";
            }

            RefreshControlAvailability();
        }

        if (propertyName == nameof(AppSessionCoordinator.Configuration))
        {
            OnPropertyChanged(nameof(HttpPort));
            OnPropertyChanged(nameof(SocksPort));
            OnPropertyChanged(nameof(RedirPort));
            OnPropertyChanged(nameof(TProxyPort));
            OnPropertyChanged(nameof(MixedPort));
            OnPropertyChanged(nameof(AllowLan));
            RefreshControlAvailability();
        }
    }

    protected override void DisposeCore()
    {
        _settings.PropertyChanged -= OnSettingsPropertyChanged;
    }

    private Task ExecuteUpdateSettingCommandAsync(SettingUpdate? update) => update is null
        ? Task.CompletedTask
        : UpdateSettingAsync(update.Key, update.Value);

    private Task ExecuteClearCapabilityCacheCommandAsync() => ReprobeCapabilitiesAsync();

    private Task RunControlOperationAsync(
        Func<CancellationToken, Task> operation,
        string successMessage,
        CancellationToken cancellationToken)
    {
        OperationMessage = null;
        return ExecuteAsync(async token =>
        {
            await operation(token);
            OperationMessage = successMessage;
        }, cancellationToken);
    }

    private async Task UpdateStartupTaskAsync(bool enabled)
    {
        try
        {
            StartupTask startupTask = await StartupTask.GetAsync(StartupTaskId);
            if (!enabled)
            {
                startupTask.Disable();
                _settings.StartWithWindows = false;
                return;
            }

            StartupTaskState state = startupTask.State;
            if (state is not StartupTaskState.Enabled and not StartupTaskState.EnabledByPolicy)
            {
                state = await startupTask.RequestEnableAsync();
            }

            _settings.StartWithWindows = state is
                StartupTaskState.Enabled or
                StartupTaskState.EnabledByPolicy;
            if (!_settings.StartWithWindows)
            {
                ErrorMessage = state == StartupTaskState.DisabledByUser
                    ? "Windows has disabled startup for Zashboard. Enable it in Windows startup app settings."
                    : "Zashboard could not be enabled as a startup app.";
            }
        }
        finally
        {
            OnPropertyChanged(nameof(StartWithWindows));
        }
    }

    private void RefreshControlAvailability()
    {
        HasControlSession = Coordinator.HasActiveSession &&
            Coordinator.SessionSnapshot.State is
                BackendConnectionState.Online or BackendConnectionState.Degraded;
        HasConfiguration = Coordinator.Configuration is not null;
        CanPatchConfiguration = HasControlSession && HasConfiguration && IsCapabilityAvailable(
            ClashCapability.ConfigurationPatch);
        CanReloadConfiguration = HasControlSession && IsCapabilityAvailable(
            ClashCapability.ConfigurationReload);
        CanUpdateConfiguration = HasControlSession && IsCapabilityAvailable(
            ClashCapability.ConfigurationUpdate);
        CanUpdateGeoData = HasControlSession && IsCapabilityAvailable(
            ClashCapability.GeoDataUpdate);
        CanRestartCore = HasControlSession && IsCapabilityAvailable(
            ClashCapability.CoreRestart);
        CanUpgradeCore = HasControlSession && Coordinator.ActiveProfile?.DisableCoreUpgrade != true &&
            IsCapabilityAvailable(ClashCapability.CoreUpgrade);
        OnPropertyChanged(nameof(ControllerConfigurationStatus));
        OnPropertyChanged(nameof(ListenerSettingsStatus));
    }

    private bool IsCapabilityAvailable(ClashCapability capability) =>
        !Coordinator.SessionSnapshot.Capabilities.TryGetValue(
            capability,
            out CapabilityObservation? observation) ||
        observation.Support != CapabilitySupport.Unsupported;

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        OnPropertyChanged(args.PropertyName);
        if (args.PropertyName == nameof(AppSettingsState.DelayTimeoutMilliseconds))
        {
            OnPropertyChanged(nameof(DelayTimeoutSeconds));
        }
    }

    private static string GetPackageVersion()
    {
        try
        {
            PackageVersion version = Package.Current.Id.Version;
            return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }
        catch (InvalidOperationException)
        {
            return "Development";
        }
    }
}
