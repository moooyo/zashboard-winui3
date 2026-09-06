using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;
using Zashboard.App.Controls;
using Zashboard.App.ViewModels;
using Zashboard.App.Views;
using Zashboard.Core.Backends;

namespace Zashboard.App.Services;

public sealed partial class PageViewModelBinder : IDisposable
{
    private readonly AppSessionCoordinator _coordinator;
    private readonly BackendSetupViewModel _backendSetup;
    private readonly OverviewViewModel _overview;
    private readonly ProxiesViewModel _proxies;
    private readonly ConnectionsViewModel _connections;
    private readonly RulesViewModel _rules;
    private readonly LogsViewModel _logs;
    private readonly SettingsViewModel _settings;
    private readonly Dictionary<FrameworkElement, BindingRegistration> _registrations = [];
    private ShellPage? _shell;
    private int _disposeState;

    public PageViewModelBinder(
        AppSessionCoordinator coordinator,
        BackendSetupViewModel backendSetup,
        OverviewViewModel overview,
        ProxiesViewModel proxies,
        ConnectionsViewModel connections,
        RulesViewModel rules,
        LogsViewModel logs,
        SettingsViewModel settings)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _backendSetup = backendSetup ?? throw new ArgumentNullException(nameof(backendSetup));
        _overview = overview ?? throw new ArgumentNullException(nameof(overview));
        _proxies = proxies ?? throw new ArgumentNullException(nameof(proxies));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _logs = logs ?? throw new ArgumentNullException(nameof(logs));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _overview.SetActive(false);
        _connections.SetActive(false);
        _logs.SetActive(false);
    }

    public void Attach(ShellPage shell)
    {
        ObjectDisposedException.ThrowIf(_disposeState != 0, this);
        ArgumentNullException.ThrowIfNull(shell);
        if (ReferenceEquals(_shell, shell))
        {
            return;
        }

        DetachShell();
        _shell = shell;
        _shell.NavigationFrame.Navigated += OnFrameNavigated;
        _shell.BackendActivationRequested += OnBackendActivationRequested;
        _shell.CancelOperationRequested += OnCancelOperationRequested;
        _shell.SetBackendItemsSource(_backendSetup.SavedBackends);
        _coordinator.PropertyChanged += OnCoordinatorPropertyChanged;
        UpdateShellMessage();
        UpdateGlobalOperationMessage();
        UpdateOperationAvailability();

        if (_shell.NavigationFrame.Content is FrameworkElement content)
        {
            Bind(content);
        }
    }

    public void Bind(FrameworkElement page)
    {
        ObjectDisposedException.ThrowIf(_disposeState != 0, this);
        ArgumentNullException.ThrowIfNull(page);
        if (_registrations.ContainsKey(page))
        {
            return;
        }

        BindingRegistration registration = new(page, Release);
        _registrations.Add(page, registration);
        switch (page)
        {
            case BackendSetupPage backendPage:
                BindBackendSetup(backendPage, registration);
                break;
            case OverviewPage overviewPage:
                BindOverview(overviewPage, registration);
                break;
            case ProxiesPage proxiesPage:
                BindProxies(proxiesPage, registration);
                break;
            case ConnectionsPage connectionsPage:
                BindConnections(connectionsPage, registration);
                break;
            case RulesPage rulesPage:
                BindRules(rulesPage, registration);
                break;
            case LogsPage logsPage:
                BindLogs(logsPage, registration);
                break;
            case SettingsPage settingsPage:
                BindSettings(settingsPage, registration);
                break;
        }

        UpdatePageOperationAvailability(page);
    }

    private void BindBackendSetup(BackendSetupPage page, BindingRegistration registration)
    {
        page.ViewModel = _backendSetup;
        BindCollection(_backendSetup.SavedBackends, page.SavedBackends, registration);

        EventHandler<BackendConnectRequestedEventArgs> connect = async (_, args) =>
        {
            Guid? profileId = null;
            if (args.BackendId is not null)
            {
                if (!Guid.TryParse(args.BackendId, out Guid parsedId) || parsedId == Guid.Empty)
                {
                    page.ShowMessage(
                        "Backend update failed",
                        "The selected backend identifier is invalid.",
                        InfoBarSeverity.Error);
                    return;
                }

                profileId = parsedId;
            }

            Guid? savedProfileId = await _backendSetup.ConnectAsync(
                args.Name,
                args.ControllerUri,
                args.Secret,
                profileId,
                args.CredentialUpdate);
            if (savedProfileId.HasValue)
            {
                page.HandleBackendSaved(savedProfileId.Value.ToString("D"));
            }
        };
        EventHandler<BackendSelectedEventArgs> activate = async (_, args) =>
            await _backendSetup.ActivateAsync(args.BackendId);
        EventHandler retry = async (_, _) =>
        {
            try
            {
                await _coordinator.RunUserOperationAsync(
                    _coordinator.InitializeAsync,
                    allowCancellation: false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                page.ShowMessage("Saved backends could not be loaded", exception.Message, InfoBarSeverity.Error);
            }
        };
        EventHandler<BackendSelectedEventArgs> remove = async (_, args) =>
        {
            if (await _backendSetup.RemoveAsync(args.BackendId))
            {
                page.HandleBackendRemoved(args.BackendId);
            }
        };
        page.ConnectRequested += connect;
        page.ActivateRequested += activate;
        page.RetryProfilesRequested += retry;
        page.RemoveRequested += remove;
        registration.Add(() => page.ConnectRequested -= connect);
        registration.Add(() => page.ActivateRequested -= activate);
        registration.Add(() => page.RetryProfilesRequested -= retry);
        registration.Add(() => page.RemoveRequested -= remove);
        BindErrors(
            _backendSetup,
            (title, message) => page.ShowMessage(title, message, InfoBarSeverity.Error),
            page.ClearErrorMessage,
            registration);
    }

    private void BindOverview(OverviewPage page, BindingRegistration registration)
    {
        _overview.SetActive(true);
        registration.Add(() => _overview.SetActive(IsActivePage<OverviewPage>(page)));
        page.ViewModel = _overview;
        BindCollection(_overview.RecentConnections, page.RecentConnections, registration);
        BindCollection(_overview.Modes, page.ModeOptions, registration);
        EventHandler refresh = async (_, _) => await _overview.RefreshAsync();
        EventHandler<ModeChangedEventArgs> mode = async (_, args) =>
            await _overview.SetModeAsync(args.Mode);
        EventHandler<TunChangedEventArgs> tun = async (_, args) =>
            await _overview.SetTunEnabledAsync(args.IsEnabled);
        EventHandler refreshHonkStatistics = async (_, _) =>
            await _overview.RefreshHonkStatisticsAsync();
        PropertyChangedEventHandler changed = (_, args) =>
        {
            switch (args.PropertyName)
            {
                case nameof(OverviewViewModel.TrafficRevision):
                    UpdateOverviewTraffic(page);
                    UpdateOverviewTrafficHistory(page);
                    break;
                case nameof(OverviewViewModel.MemoryRevision):
                    UpdateOverviewSession(page);
                    UpdateOverviewMemoryHistory(page);
                    break;
                case nameof(OverviewViewModel.SessionState):
                case nameof(OverviewViewModel.ConnectionCount):
                case nameof(OverviewViewModel.CoreVersion):
                case nameof(OverviewViewModel.SessionStatusDetail):
                case nameof(OverviewViewModel.IsOnline):
                    UpdateOverviewSession(page);
                    break;
                case nameof(OverviewViewModel.SelectedMode):
                case nameof(OverviewViewModel.IsTunEnabled):
                case nameof(OverviewViewModel.CanChangeMode):
                case nameof(OverviewViewModel.CanChangeTun):
                    UpdateOverviewControls(page);
                    break;
                case nameof(OverviewViewModel.IsHonkStatisticsVisible):
                case nameof(OverviewViewModel.CanRefreshHonkStatistics):
                case nameof(OverviewViewModel.HonkStatisticsStatus):
                case nameof(OverviewViewModel.HonkTotalConnections):
                case nameof(OverviewViewModel.HonkActiveConnections):
                case nameof(OverviewViewModel.HonkUpload):
                case nameof(OverviewViewModel.HonkDownload):
                case nameof(OverviewViewModel.HonkErrors):
                    UpdateOverviewHonkStatistics(page);
                    break;
            }
        };
        page.RefreshRequested += refresh;
        page.ModeChanged += mode;
        page.TunChanged += tun;
        page.RefreshHonkStatisticsRequested += refreshHonkStatistics;
        _overview.PropertyChanged += changed;
        registration.Add(() => page.RefreshRequested -= refresh);
        registration.Add(() => page.ModeChanged -= mode);
        registration.Add(() => page.TunChanged -= tun);
        registration.Add(() => page.RefreshHonkStatisticsRequested -= refreshHonkStatistics);
        registration.Add(() => _overview.PropertyChanged -= changed);
        BindErrors(
            _overview,
            (title, message) => page.ShowMessage(title, message, InfoBarSeverity.Error),
            page.ClearErrorMessage,
            registration);
        UpdateOverview(page);
    }

    private void BindProxies(ProxiesPage page, BindingRegistration registration)
    {
        page.ViewModel = _proxies;
        BindCollection(_proxies.ProxyGroupCards, page.ProxyGroupCards, registration);
        BindCollection(_proxies.Providers, page.Providers, registration);

        EventHandler refresh = async (_, _) => await _proxies.RefreshAsync();
        EventHandler testAll = async (_, _) => await _proxies.TestAllCommand.ExecuteAsync(null);
        EventHandler updateProviders = async (_, _) =>
            await _proxies.UpdateProvidersCommand.ExecuteAsync(null);
        EventHandler checkProviders = async (_, _) =>
            await _proxies.CheckProvidersCommand.ExecuteAsync(null);
        EventHandler<ProxyProviderRequestedEventArgs> updateProvider = async (_, args) =>
            await _proxies.UpdateProviderAsync(args.ProviderName);
        EventHandler<ProxyProviderRequestedEventArgs> checkProvider = async (_, args) =>
            await _proxies.CheckProviderAsync(args.ProviderName);
        EventHandler<ProxyGroupRequestedEventArgs> testGroup = async (_, args) =>
            await _proxies.TestGroupAsync(args.GroupName);
        EventHandler<ProxyTestRequestedEventArgs> testProxy = async (_, args) =>
            await _proxies.TestProxyAsync(
                args.GroupName,
                args.ProxyName,
                args.ProviderName);
        EventHandler<ProxySelectionRequestedEventArgs> selectProxy = async (_, args) =>
            await _proxies.SelectProxyAsync(args.GroupName, args.ProxyName);
        EventHandler<ProxyGroupRequestedEventArgs> clearFixedProxy = async (_, args) =>
            await _proxies.ClearFixedProxyAsync(args.GroupName);
        EventHandler refreshSmartWeights = async (_, _) =>
            await _proxies.RefreshSmartWeightsAsync();
        EventHandler resetSmartWeights = async (_, _) =>
            await _proxies.ResetSmartWeightsAsync();
        PropertyChangedEventHandler changed = (_, _) => UpdateProxyCapabilities(page);

        page.RefreshRequested += refresh;
        page.TestAllRequested += testAll;
        page.UpdateProvidersRequested += updateProviders;
        page.CheckProvidersRequested += checkProviders;
        page.UpdateProviderRequested += updateProvider;
        page.CheckProviderRequested += checkProvider;
        page.TestGroupRequested += testGroup;
        page.TestProxyRequested += testProxy;
        page.ProxySelectionRequested += selectProxy;
        page.ClearFixedProxyRequested += clearFixedProxy;
        page.RefreshSmartWeightsRequested += refreshSmartWeights;
        page.ResetSmartWeightsRequested += resetSmartWeights;
        _proxies.PropertyChanged += changed;
        registration.Add(() => page.RefreshRequested -= refresh);
        registration.Add(() => page.TestAllRequested -= testAll);
        registration.Add(() => page.UpdateProvidersRequested -= updateProviders);
        registration.Add(() => page.CheckProvidersRequested -= checkProviders);
        registration.Add(() => page.UpdateProviderRequested -= updateProvider);
        registration.Add(() => page.CheckProviderRequested -= checkProvider);
        registration.Add(() => page.TestGroupRequested -= testGroup);
        registration.Add(() => page.TestProxyRequested -= testProxy);
        registration.Add(() => page.ProxySelectionRequested -= selectProxy);
        registration.Add(() => page.ClearFixedProxyRequested -= clearFixedProxy);
        registration.Add(() => page.RefreshSmartWeightsRequested -= refreshSmartWeights);
        registration.Add(() => page.ResetSmartWeightsRequested -= resetSmartWeights);
        registration.Add(() => _proxies.PropertyChanged -= changed);
        BindErrors(
            _proxies,
            (title, message) => page.ShowMessage(title, message, InfoBarSeverity.Error),
            page.ClearErrorMessage,
            registration);
        UpdateProxyCapabilities(page);
    }

    private void BindConnections(ConnectionsPage page, BindingRegistration registration)
    {
        _connections.SetActive(true);
        registration.Add(() => _connections.SetActive(IsActivePage<ConnectionsPage>(page)));
        page.ViewModel = _connections;
        BindCollection(_connections.Connections, page.Connections, registration);
        EventHandler refresh = async (_, _) => await _connections.RefreshAsync();
        EventHandler disconnectAll = async (_, _) => await _connections.DisconnectAllAsync();
        EventHandler<ConnectionRequestedEventArgs> disconnect = async (_, args) =>
            await _connections.DisconnectAsync(args.ConnectionId);
        EventHandler<ConnectionRequestedEventArgs> block = async (_, args) =>
            await _connections.BlockAsync(args.ConnectionId);
        EventHandler<SearchRequestedEventArgs> search = async (_, args) =>
            await _connections.SetQueryAsync(args.Query);
        EventHandler<PauseRequestedEventArgs> pause = async (_, args) =>
            await _connections.SetPausedAsync(args.IsPaused);
        PropertyChangedEventHandler changed = (_, args) =>
        {
            if (args.PropertyName is nameof(ConnectionsViewModel.TotalCount) or
                nameof(ConnectionsViewModel.Query) or
                nameof(ConnectionsViewModel.IsPaused))
            {
                UpdateConnections(page);
            }
        };

        page.RefreshRequested += refresh;
        page.DisconnectAllRequested += disconnectAll;
        page.DisconnectRequested += disconnect;
        page.BlockRequested += block;
        page.SearchRequested += search;
        page.PauseRequested += pause;
        _connections.PropertyChanged += changed;
        registration.Add(() => page.RefreshRequested -= refresh);
        registration.Add(() => page.DisconnectAllRequested -= disconnectAll);
        registration.Add(() => page.DisconnectRequested -= disconnect);
        registration.Add(() => page.BlockRequested -= block);
        registration.Add(() => page.SearchRequested -= search);
        registration.Add(() => page.PauseRequested -= pause);
        registration.Add(() => _connections.PropertyChanged -= changed);
        BindErrors(
            _connections,
            (title, message) => page.ShowMessage(title, message, InfoBarSeverity.Error),
            page.ClearErrorMessage,
            registration);
        UpdateConnections(page);
    }

    private void BindRules(RulesPage page, BindingRegistration registration)
    {
        page.ViewModel = _rules;
        BindCollection(_rules.Rules, page.Rules, registration);
        BindCollection(_rules.Providers, page.RuleProviders, registration);
        EventHandler refresh = async (_, _) => await _rules.RefreshAsync();
        EventHandler<RuleQueryChangedEventArgs> query = async (_, args) =>
            await _rules.SetQueryAsync(args.Query, args.Filter);
        EventHandler<RuleToggleRequestedEventArgs> toggleRule = async (_, args) =>
            await _rules.ToggleRuleAsync(args.Index, args.Identifier, args.Disabled);
        EventHandler updateProviders = async (_, _) =>
            await _rules.UpdateProvidersCommand.ExecuteAsync(null);
        EventHandler<RuleProviderRequestedEventArgs> updateProvider = async (_, args) =>
            await _rules.UpdateProviderAsync(args.ProviderName);
        PropertyChangedEventHandler changed = (_, _) =>
        {
            UpdateRuleCapabilities(page);
            page.ApplyViewState(_rules.Query, _rules.Filter);
        };
        page.RefreshRequested += refresh;
        page.QueryChanged += query;
        page.RuleToggleRequested += toggleRule;
        page.UpdateProvidersRequested += updateProviders;
        page.UpdateProviderRequested += updateProvider;
        _rules.PropertyChanged += changed;
        registration.Add(() => page.RefreshRequested -= refresh);
        registration.Add(() => page.QueryChanged -= query);
        registration.Add(() => page.RuleToggleRequested -= toggleRule);
        registration.Add(() => page.UpdateProvidersRequested -= updateProviders);
        registration.Add(() => page.UpdateProviderRequested -= updateProvider);
        registration.Add(() => _rules.PropertyChanged -= changed);
        BindErrors(
            _rules,
            (title, message) => page.ShowMessage(title, message, InfoBarSeverity.Error),
            page.ClearErrorMessage,
            registration);
        UpdateRuleCapabilities(page);
        page.ApplyViewState(_rules.Query, _rules.Filter);
    }

    private void BindLogs(LogsPage page, BindingRegistration registration)
    {
        _logs.SetActive(true);
        registration.Add(() => _logs.SetActive(IsActivePage<LogsPage>(page)));
        page.ViewModel = _logs;
        page.AttachLogEntries(_logs.LogEntries);
        registration.Add(page.DetachLogEntries);
        EventHandler clear = async (_, _) => await _logs.ClearAsync();
        EventHandler<LogQueryChangedEventArgs> query = async (_, args) =>
            await _logs.SetQueryAsync(args.Query, args.Level);
        EventHandler<LogToggleRequestedEventArgs> pause = async (_, args) =>
            await _logs.SetPausedAsync(args.IsEnabled);
        EventHandler<LogToggleRequestedEventArgs> follow = async (_, args) =>
            await _logs.SetFollowTailAsync(args.IsEnabled);
        EventHandler<LogCopyRequestedEventArgs> copy = (_, args) =>
        {
            DataPackage package = new();
            package.SetText(LogsViewModel.BuildClipboardText(args.Entries));
            Clipboard.SetContent(package);
        };
        PropertyChangedEventHandler changed = (_, args) =>
        {
            if (args.PropertyName is nameof(LogsViewModel.Query) or
                nameof(LogsViewModel.Level) or
                nameof(LogsViewModel.IsPaused) or
                nameof(LogsViewModel.FollowTail) or
                nameof(LogsViewModel.SourceEntryCount) or
                nameof(LogsViewModel.DroppedLogCount))
            {
                page.ApplyViewState(
                    _logs.Query,
                    _logs.Level,
                    _logs.IsPaused,
                    _logs.FollowTail,
                    _logs.SourceEntryCount,
                    _logs.DroppedLogCount);
            }
        };

        page.ClearRequested += clear;
        page.QueryChanged += query;
        page.PauseRequested += pause;
        page.FollowTailChanged += follow;
        page.CopyRequested += copy;
        _logs.PropertyChanged += changed;
        registration.Add(() => page.ClearRequested -= clear);
        registration.Add(() => page.QueryChanged -= query);
        registration.Add(() => page.PauseRequested -= pause);
        registration.Add(() => page.FollowTailChanged -= follow);
        registration.Add(() => page.CopyRequested -= copy);
        registration.Add(() => _logs.PropertyChanged -= changed);
        BindErrors(
            _logs,
            (title, message) => page.ShowMessage(title, message, InfoBarSeverity.Error),
            page.ClearErrorMessage,
            registration);
        page.ApplyViewState(
            _logs.Query,
            _logs.Level,
            _logs.IsPaused,
            _logs.FollowTail,
            _logs.SourceEntryCount,
            _logs.DroppedLogCount);
    }

    private void BindSettings(SettingsPage page, BindingRegistration registration)
    {
        page.ViewModel = _settings;
        page.SetAppVersion(_settings.AppVersion);
        page.ApplySettings(_settings);
        page.ApplyControllerState(_settings);
        BindCollection(_settings.DnsAnswers, page.DnsAnswers, registration);
        EventHandler clear = async (_, _) => await _settings.ClearCapabilityCacheAsync();
        EventHandler reload = async (_, _) => await _settings.ReloadConfigurationAsync();
        EventHandler<ConfigurationUpdateRequestedEventArgs> updateConfiguration = async (_, args) =>
            await _settings.UpdateConfigurationAsync(args.Path, args.Payload, args.Force);
        EventHandler<NetworkConfigurationRequestedEventArgs> updateNetwork = async (_, args) =>
        {
            bool updated = await _settings.ApplyNetworkConfigurationAsync(
                new NetworkConfigurationUpdate(
                    args.HttpPort,
                    args.SocksPort,
                    args.RedirPort,
                    args.TProxyPort,
                    args.MixedPort,
                    args.AllowLan));
            if (updated)
            {
                page.MarkListenerSettingsSaved(_settings);
            }
        };
        EventHandler<DnsQueryRequestedEventArgs> queryDns = async (_, args) =>
            await _settings.QueryDnsAsync(args.Name, args.Type);
        EventHandler<DnsAnswerCopyRequestedEventArgs> copyDnsAnswer = (_, args) =>
        {
            try
            {
                DataPackage package = new();
                package.SetText(args.Value);
                Clipboard.SetContent(package);
                page.ShowDnsCopyConfirmation();
            }
            catch (Exception exception)
            {
                page.ShowMessage(
                    "DNS answer could not be copied",
                    exception.Message,
                    InfoBarSeverity.Error);
            }
        };
        EventHandler flushDns = async (_, _) => await _settings.FlushDnsCacheAsync();
        EventHandler flushFakeIp = async (_, _) => await _settings.FlushFakeIpCacheAsync();
        EventHandler updateGeo = async (_, _) => await _settings.UpdateGeoDataAsync();
        EventHandler restart = async (_, _) => await _settings.RestartCoreAsync();
        EventHandler<CoreUpgradeRequestedEventArgs> upgrade = async (_, args) =>
            await _settings.UpgradeCoreAsync(args.Channel);
        EventHandler<SettingChangedEventArgs> changed = async (_, args) =>
            await _settings.UpdateSettingAsync(args.Key, args.Value);
        PropertyChangedEventHandler settingsChanged = (_, args) =>
        {
            if (args.PropertyName is
                nameof(SettingsViewModel.StartWithWindows) or
                nameof(SettingsViewModel.CloseBehavior) or
                nameof(SettingsViewModel.Theme) or
                nameof(SettingsViewModel.MicaBackdrop) or
                nameof(SettingsViewModel.LogBufferSize) or
                nameof(SettingsViewModel.DelayTestUrl) or
                nameof(SettingsViewModel.DelayTimeoutSeconds))
            {
                page.ApplySetting(_settings, args.PropertyName);
            }

            if (args.PropertyName is
                nameof(SettingsViewModel.HasControlSession) or
                nameof(SettingsViewModel.HasConfiguration) or
                nameof(SettingsViewModel.CanPatchConfiguration) or
                nameof(SettingsViewModel.CanReloadConfiguration) or
                nameof(SettingsViewModel.CanUpdateConfiguration) or
                nameof(SettingsViewModel.CanUpdateGeoData) or
                nameof(SettingsViewModel.CanRestartCore) or
                nameof(SettingsViewModel.CanUpgradeCore) or
                nameof(SettingsViewModel.ControllerConfigurationStatus) or
                nameof(SettingsViewModel.ListenerSettingsStatus))
            {
                page.ApplyControllerAvailability(_settings);
            }

            if (args.PropertyName is
                nameof(SettingsViewModel.ControllerSessionRevision) or
                nameof(SettingsViewModel.HttpPort) or
                nameof(SettingsViewModel.SocksPort) or
                nameof(SettingsViewModel.RedirPort) or
                nameof(SettingsViewModel.TProxyPort) or
                nameof(SettingsViewModel.MixedPort) or
                nameof(SettingsViewModel.AllowLan))
            {
                page.ApplyListenerSettings(_settings);
            }

            if (args.PropertyName == nameof(SettingsViewModel.DnsSummary))
            {
                page.ApplyDnsStatus(_settings);
            }

            if (args.PropertyName == nameof(SettingsViewModel.OperationMessage) &&
                !string.IsNullOrWhiteSpace(_settings.OperationMessage))
            {
                page.ShowMessage(
                    "Operation completed",
                    _settings.OperationMessage,
                    InfoBarSeverity.Success);
            }
        };
        page.ClearCapabilityCacheRequested += clear;
        page.ReloadConfigurationRequested += reload;
        page.UpdateConfigurationRequested += updateConfiguration;
        page.NetworkConfigurationRequested += updateNetwork;
        page.QueryDnsRequested += queryDns;
        page.DnsAnswerCopyRequested += copyDnsAnswer;
        page.FlushDnsCacheRequested += flushDns;
        page.FlushFakeIpCacheRequested += flushFakeIp;
        page.UpdateGeoDataRequested += updateGeo;
        page.RestartCoreRequested += restart;
        page.UpgradeCoreRequested += upgrade;
        page.SettingChanged += changed;
        _settings.PropertyChanged += settingsChanged;
        _ = _settings.RefreshStartupTaskStateAsync();
        registration.Add(() => page.ClearCapabilityCacheRequested -= clear);
        registration.Add(() => page.ReloadConfigurationRequested -= reload);
        registration.Add(() => page.UpdateConfigurationRequested -= updateConfiguration);
        registration.Add(() => page.NetworkConfigurationRequested -= updateNetwork);
        registration.Add(() => page.QueryDnsRequested -= queryDns);
        registration.Add(() => page.DnsAnswerCopyRequested -= copyDnsAnswer);
        registration.Add(() => page.FlushDnsCacheRequested -= flushDns);
        registration.Add(() => page.FlushFakeIpCacheRequested -= flushFakeIp);
        registration.Add(() => page.UpdateGeoDataRequested -= updateGeo);
        registration.Add(() => page.RestartCoreRequested -= restart);
        registration.Add(() => page.UpgradeCoreRequested -= upgrade);
        registration.Add(() => page.SettingChanged -= changed);
        registration.Add(() => _settings.PropertyChanged -= settingsChanged);
        BindErrors(
            _settings,
            (title, message) => page.ShowMessage(title, message, InfoBarSeverity.Error),
            page.ClearErrorMessage,
            registration);
    }

    private void OnFrameNavigated(object sender, NavigationEventArgs args)
    {
        if (args.Content is FrameworkElement page)
        {
            Bind(page);
        }
    }

    private void OnCoordinatorPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(AppSessionCoordinator.IsUserOperationRunning) or
            nameof(AppSessionCoordinator.CanCancelUserOperation) or
            nameof(AppSessionCoordinator.CanWriteProfiles) or
            nameof(AppSessionCoordinator.ProfileLoadError))
        {
            UpdateOperationAvailability();
        }

        if (args.PropertyName is nameof(AppSessionCoordinator.LastErrorMessage) or
            nameof(AppSessionCoordinator.SessionSnapshot))
        {
            UpdateShellMessage();
        }

        if (args.PropertyName == nameof(AppSessionCoordinator.LastUserOperationErrorMessage))
        {
            UpdateGlobalOperationMessage();
        }
    }

    private void OnCancelOperationRequested(object? sender, EventArgs args) =>
        _coordinator.CancelUserOperation();

    private void UpdateGlobalOperationMessage()
    {
        if (string.IsNullOrWhiteSpace(_coordinator.LastUserOperationErrorMessage))
        {
            _shell?.ClearOperationMessage();
        }
        else
        {
            _shell?.ShowOperationMessage(
                "Controller operation failed",
                _coordinator.LastUserOperationErrorMessage,
                InfoBarSeverity.Error);
        }
    }

    private void UpdateOperationAvailability()
    {
        foreach (FrameworkElement page in _registrations.Keys)
        {
            UpdatePageOperationAvailability(page);
        }

        _shell?.SetOperationInProgress(
            _coordinator.IsUserOperationRunning,
            _coordinator.CanCancelUserOperation,
            _coordinator.CanWriteProfiles);
    }

    private void UpdatePageOperationAvailability(FrameworkElement page)
    {
        switch (page)
        {
            case BackendSetupPage backendPage:
                backendPage.ApplyOperationAvailability(
                    _coordinator.IsUserOperationRunning,
                    _coordinator.CanWriteProfiles,
                    _coordinator.ProfileLoadError);
                break;
            case OverviewPage overviewPage:
                UpdateOverviewSession(overviewPage);
                UpdateOverviewControls(overviewPage);
                UpdateOverviewHonkStatistics(overviewPage);
                break;
            case ProxiesPage proxiesPage:
                UpdateProxyCapabilities(proxiesPage);
                break;
            case ConnectionsPage connectionsPage:
                UpdateConnections(connectionsPage);
                break;
            case RulesPage rulesPage:
                UpdateRuleCapabilities(rulesPage);
                break;
            case SettingsPage settingsPage:
                settingsPage.ApplyControllerAvailability(_settings);
                break;
        }
    }

    private bool IsActivePage<TPage>(FrameworkElement releasedPage) where TPage : FrameworkElement =>
        _shell?.NavigationFrame.Content is TPage currentPage &&
        !ReferenceEquals(currentPage, releasedPage) &&
        _registrations.ContainsKey(currentPage);

    private async void OnBackendActivationRequested(
        object? sender,
        BackendSelectedEventArgs args)
    {
        _shell?.ClearOperationMessage();
        await _backendSetup.ActivateAsync(args.BackendId);
        if (_shell is not null && !string.IsNullOrWhiteSpace(_backendSetup.ErrorMessage))
        {
            _shell.ShowOperationMessage(
                "Backend switch failed",
                _backendSetup.ErrorMessage,
                InfoBarSeverity.Error);
        }
    }

    private void UpdateShellMessage()
    {
        if (_shell is null)
        {
            return;
        }

        string? message = _coordinator.LastErrorMessage;
        if (string.IsNullOrWhiteSpace(message))
        {
            _shell.ClearSessionMessage();
            return;
        }

        InfoBarSeverity severity = _coordinator.SessionSnapshot.State ==
            BackendConnectionState.Unauthorized
            ? InfoBarSeverity.Error
            : InfoBarSeverity.Warning;
        _shell.ShowSessionMessage("Backend session", message, severity);
    }

    private void UpdateOverview(OverviewPage page)
    {
        UpdateOverviewSession(page);
        UpdateOverviewTraffic(page);
        UpdateOverviewControls(page);
        UpdateOverviewTrafficHistory(page);
        UpdateOverviewMemoryHistory(page);
        UpdateOverviewHonkStatistics(page);
    }

    private void UpdateOverviewSession(OverviewPage page)
    {
        BackendConnectionState state = _coordinator.SessionSnapshot.State;
        Color color = state switch
        {
            BackendConnectionState.Online => Colors.ForestGreen,
            BackendConnectionState.Connecting => Colors.DodgerBlue,
            BackendConnectionState.Degraded => Colors.DarkOrange,
            BackendConnectionState.Unauthorized => Colors.Crimson,
            BackendConnectionState.OfflineRetrying => Colors.DarkOrange,
            _ => Colors.Gray,
        };
        InfoBarSeverity statusSeverity = state switch
        {
            BackendConnectionState.Unauthorized => InfoBarSeverity.Error,
            BackendConnectionState.Degraded or BackendConnectionState.OfflineRetrying =>
                InfoBarSeverity.Warning,
            _ => InfoBarSeverity.Informational,
        };
        page.SetSessionSummary(
            _overview.SessionState,
            _overview.ConnectionCount,
            _overview.Memory,
            _overview.CoreVersion,
            _overview.SessionStatusDetail,
            statusSeverity,
            new SolidColorBrush(color),
            _overview.IsOnline,
            !_coordinator.IsUserOperationRunning && _coordinator.HasActiveSession &&
                _coordinator.SessionSnapshot.State != BackendConnectionState.Unauthorized);
    }

    private void UpdateOverviewTraffic(OverviewPage page)
    {
        page.SetTrafficRates(
            _overview.DownloadRate,
            _overview.UploadRate,
            _overview.DownloadTotal,
            _overview.UploadTotal);
    }

    private void UpdateOverviewControls(OverviewPage page)
    {
        page.ApplyControllerState(
            _overview.SelectedMode,
            _overview.IsTunEnabled,
            _overview.CanChangeMode && !_coordinator.IsUserOperationRunning,
            _overview.CanChangeTun && !_coordinator.IsUserOperationRunning);
    }

    private void UpdateOverviewTrafficHistory(OverviewPage page)
    {
        page.SetTrafficHistory(
            _overview.TrafficHistory,
            _overview.DownloadRate,
            _overview.UploadRate);
    }

    private void UpdateOverviewMemoryHistory(OverviewPage page)
    {
        page.SetMemoryHistory(
            _overview.MemoryHistory,
            _overview.Memory);
    }

    private void UpdateOverviewHonkStatistics(OverviewPage page)
    {
        page.SetHonkRuntimeStatistics(
            _overview.IsHonkStatisticsVisible,
            _overview.CanRefreshHonkStatistics && !_coordinator.IsUserOperationRunning,
            _overview.HonkStatisticsStatus,
            _overview.HonkTotalConnections,
            _overview.HonkActiveConnections,
            _overview.HonkUpload,
            _overview.HonkDownload,
            _overview.HonkErrors);
    }

    private void UpdateProxyCapabilities(ProxiesPage page)
    {
        page.ApplyProviderCapabilities(
            _proxies.CanUpdateProviders && !_coordinator.IsUserOperationRunning,
            _proxies.CanCheckProviders && !_coordinator.IsUserOperationRunning,
            !_coordinator.IsUserOperationRunning && _coordinator.SessionSnapshot.State is
                BackendConnectionState.Online or BackendConnectionState.Degraded);
    }

    private void UpdateRuleCapabilities(RulesPage page)
    {
        page.ApplyProviderCapabilities(
            _rules.CanUpdateProviders && !_coordinator.IsUserOperationRunning,
            !_coordinator.IsUserOperationRunning && _coordinator.SessionSnapshot.State is
                BackendConnectionState.Online or BackendConnectionState.Degraded);
    }

    private void UpdateConnections(ConnectionsPage page)
    {
        page.ApplyConnectionState(
            _connections.TotalCount,
            !_coordinator.IsUserOperationRunning && _coordinator.HasActiveSession &&
                _coordinator.SessionSnapshot.State is
                BackendConnectionState.Online or BackendConnectionState.Degraded);
        page.ApplyViewState(_connections.Query, _connections.IsPaused);
    }

    private static void BindErrors(
        ViewModelBase viewModel,
        Action<string, string> show,
        Action clear,
        BindingRegistration registration)
    {
        PropertyChangedEventHandler changed = (_, args) =>
        {
            if (args.PropertyName != nameof(ViewModelBase.ErrorMessage))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(viewModel.ErrorMessage))
            {
                clear();
            }
            else
            {
                show("Operation failed", viewModel.ErrorMessage);
            }
        };
        viewModel.PropertyChanged += changed;
        registration.Add(() => viewModel.PropertyChanged -= changed);
        if (!string.IsNullOrWhiteSpace(viewModel.ErrorMessage))
        {
            show("Operation failed", viewModel.ErrorMessage);
        }
    }

    private static void BindCollection<T>(
        ObservableCollection<T> source,
        ObservableCollection<T> destination,
        BindingRegistration registration)
    {
        CopyCollection(source, destination);
        NotifyCollectionChangedEventHandler changed = (_, args) =>
            ApplyCollectionChange(source, destination, args);
        source.CollectionChanged += changed;
        registration.Add(() => source.CollectionChanged -= changed);
    }

    private static void ApplyCollectionChange<T>(
        ObservableCollection<T> source,
        ObservableCollection<T> destination,
        NotifyCollectionChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Add when args.NewStartingIndex >= 0:
                for (int index = 0; index < (args.NewItems?.Count ?? 0); index++)
                {
                    destination.Insert(args.NewStartingIndex + index, (T)args.NewItems![index]!);
                }

                return;
            case NotifyCollectionChangedAction.Remove when args.OldStartingIndex >= 0:
                for (int index = 0; index < (args.OldItems?.Count ?? 0); index++)
                {
                    destination.RemoveAt(args.OldStartingIndex);
                }

                return;
            case NotifyCollectionChangedAction.Replace when
                args.NewStartingIndex >= 0 && args.NewItems is not null:
                for (int index = 0; index < args.NewItems.Count; index++)
                {
                    destination[args.NewStartingIndex + index] = (T)args.NewItems[index]!;
                }

                return;
            case NotifyCollectionChangedAction.Move when
                args.OldItems?.Count == 1 &&
                args.OldStartingIndex >= 0 &&
                args.NewStartingIndex >= 0:
                destination.Move(args.OldStartingIndex, args.NewStartingIndex);
                return;
            default:
                CopyCollection(source, destination);
                return;
        }
    }

    private static void CopyCollection<T>(
        IEnumerable<T> source,
        ObservableCollection<T> destination)
    {
        CollectionBatch.Replace(destination, source);
    }

    private void Release(FrameworkElement page)
    {
        if (_registrations.Remove(page, out BindingRegistration? registration))
        {
            registration.Dispose();
        }
    }

    private void DetachShell()
    {
        if (_shell is null)
        {
            return;
        }

        _shell.NavigationFrame.Navigated -= OnFrameNavigated;
        _shell.BackendActivationRequested -= OnBackendActivationRequested;
        _shell.CancelOperationRequested -= OnCancelOperationRequested;
        _shell.SetBackendItemsSource(null);
        _coordinator.PropertyChanged -= OnCoordinatorPropertyChanged;
        _shell = null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        DetachShell();
        BindingRegistration[] registrations = _registrations.Values.ToArray();
        _registrations.Clear();
        foreach (BindingRegistration registration in registrations)
        {
            registration.Dispose();
        }
    }

    private sealed partial class BindingRegistration : IDisposable
    {
        private readonly FrameworkElement _page;
        private readonly Action<FrameworkElement> _release;
        private readonly List<Action> _cleanup = [];
        private int _disposeState;

        public BindingRegistration(FrameworkElement page, Action<FrameworkElement> release)
        {
            _page = page;
            _release = release;
            _page.Unloaded += OnUnloaded;
        }

        public void Add(Action cleanup)
        {
            ArgumentNullException.ThrowIfNull(cleanup);
            _cleanup.Add(cleanup);
        }

        private void OnUnloaded(object sender, RoutedEventArgs args) => _release(_page);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            {
                return;
            }

            _page.Unloaded -= OnUnloaded;
            foreach (Action cleanup in _cleanup)
            {
                cleanup();
            }

            _cleanup.Clear();
        }
    }
}
