using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;
using Zashboard.App.Controls;
using Zashboard.Core.Abstractions;
using Zashboard.Core.Backends;
using Zashboard.Core.Capabilities;
using Zashboard.Core.Clash;
using Zashboard.Core.Normalization;
using Zashboard.Core.Sessions;
using Zashboard.Infrastructure.Clash;
using Zashboard.Infrastructure.Persistence;

namespace Zashboard.App.Services;

public sealed partial class AppSessionCoordinator : ObservableObject, IAsyncDisposable
{
    private static readonly TimeSpan HonkStatisticsInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LiveContactPublishInterval = TimeSpan.FromSeconds(1);

    private readonly IBackendProfileStore _profileStore;
    private readonly IBackendCredentialStore _credentialStore;
    private readonly IBackendSessionFactory _sessionFactory;
    private readonly IUiDispatcher _dispatcher;
    private readonly AppSettingsState _settings;
    private readonly TimeProvider _timeProvider;
    private readonly BulkObservableCollection<BackendProfile> _profiles = [];
    private readonly BulkObservableCollection<SessionLogEntry> _logs = [];
    private readonly Dictionary<ClashStreamKind, ClashStreamStatus> _streamStatuses = [];
    private readonly Dictionary<string, string> _resourceFailures = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly SemaphoreSlim _profileWriteGate = new(1, 1);
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _userOperationGate = new(1, 1);
    private readonly CancellationTokenSource _shutdownSource = new();
    private readonly TaskCompletionSource _disposeCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _profileStateGate = new();
    private readonly Lock _logIterationGate = new();
    private readonly HashSet<Guid> _profilesWithStoredCredentials = [];

    private BackendProfileSet _profileSet = new();
    private IBackendSession? _activeSession;
    private CancellationTokenSource? _sessionWorkSource;
    private Task[] _backgroundTasks = [];
    private BackendProfile? _activeProfile;
    private BackendSessionSnapshot _sessionSnapshot;
    private ClashConfiguration? _configuration;
    private ProxyCatalog? _proxyCatalog;
    private ProxyProviderCatalog? _proxyProviders;
    private SmartWeights? _smartWeights;
    private RuleCatalog? _ruleCatalog;
    private RuleProviderCatalog? _ruleProviders;
    private ConnectionStreamSnapshot? _connectionSnapshot;
    private ClashTrafficSample? _trafficSample;
    private ClashMemorySample? _memorySample;
    private HonkRuntimeStatistics? _honkRuntimeStatistics;
    private string? _lastErrorMessage;
    private string? _lastUserOperationErrorMessage;
    private bool _isInitialized;
    private bool _isRefreshing;
    private bool _isUserOperationRunning;
    private long _epochValue;
    private long _nextLogSequence;
    private long _logGeneration;
    private long _pendingTransportDroppedLogCount;
    private long _droppedLogCount;
    private CancellationTokenSource? _logIterationSource;
    private int _disposeState;

    public AppSessionCoordinator(
        IBackendProfileStore profileStore,
        IBackendCredentialStore credentialStore,
        IBackendSessionFactory sessionFactory,
        IUiDispatcher dispatcher,
        AppSettingsState settings,
        TimeProvider timeProvider)
    {
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _sessionSnapshot = BackendSessionSnapshot.NoBackend(
            new SessionEpoch(0),
            _timeProvider.GetUtcNow());

        Profiles = new ReadOnlyObservableCollection<BackendProfile>(_profiles);
        Logs = new ReadOnlyObservableCollection<SessionLogEntry>(_logs);
        _settings.PropertyChanged += OnSettingsPropertyChanged;
    }

    public ReadOnlyObservableCollection<BackendProfile> Profiles { get; }

    public ReadOnlyObservableCollection<SessionLogEntry> Logs { get; }

    public event EventHandler<SessionLogsChangedEventArgs>? LogsChanged;

    public SessionEpoch Epoch => new(Interlocked.Read(ref _epochValue));

    public IBackendSession? ActiveSession => Volatile.Read(ref _activeSession);

    public BackendProfile? ActiveProfile
    {
        get => _activeProfile;
        private set => SetProperty(ref _activeProfile, value);
    }

    public bool HasStoredCredential(Guid profileId)
    {
        lock (_profileStateGate)
        {
            return _profilesWithStoredCredentials.Contains(profileId);
        }
    }

    public BackendSessionSnapshot SessionSnapshot
    {
        get => _sessionSnapshot;
        private set
        {
            if (SetProperty(ref _sessionSnapshot, value))
            {
                OnPropertyChanged(nameof(HasActiveSession));
                OnPropertyChanged(nameof(IsOnline));
            }
        }
    }

    public ClashConfiguration? Configuration
    {
        get => _configuration;
        private set => SetProperty(ref _configuration, value);
    }

    public ProxyCatalog? ProxyCatalog
    {
        get => _proxyCatalog;
        private set => SetProperty(ref _proxyCatalog, value);
    }

    public ProxyProviderCatalog? ProxyProviders
    {
        get => _proxyProviders;
        private set => SetProperty(ref _proxyProviders, value);
    }

    public SmartWeights? SmartWeights
    {
        get => _smartWeights;
        private set => SetProperty(ref _smartWeights, value);
    }

    public RuleCatalog? RuleCatalog
    {
        get => _ruleCatalog;
        private set => SetProperty(ref _ruleCatalog, value);
    }

    public RuleProviderCatalog? RuleProviders
    {
        get => _ruleProviders;
        private set => SetProperty(ref _ruleProviders, value);
    }

    public ConnectionStreamSnapshot? ConnectionSnapshot
    {
        get => _connectionSnapshot;
        private set => SetProperty(ref _connectionSnapshot, value);
    }

    public ClashTrafficSample? TrafficSample
    {
        get => _trafficSample;
        private set => SetProperty(ref _trafficSample, value);
    }

    public ClashMemorySample? MemorySample
    {
        get => _memorySample;
        private set => SetProperty(ref _memorySample, value);
    }

    public HonkRuntimeStatistics? HonkRuntimeStatistics
    {
        get => _honkRuntimeStatistics;
        private set => SetProperty(ref _honkRuntimeStatistics, value);
    }

    public long DroppedLogCount
    {
        get => _droppedLogCount;
        private set => SetProperty(ref _droppedLogCount, value);
    }

    public string? LastErrorMessage
    {
        get => _lastErrorMessage;
        private set => SetProperty(ref _lastErrorMessage, value);
    }

    public string? LastUserOperationErrorMessage
    {
        get => _lastUserOperationErrorMessage;
        private set => SetProperty(ref _lastUserOperationErrorMessage, value);
    }

    public bool IsInitialized
    {
        get => _isInitialized;
        private set => SetProperty(ref _isInitialized, value);
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set => SetProperty(ref _isRefreshing, value);
    }

    public bool HasActiveSession => ActiveSession is not null;

    public bool IsOnline => SessionSnapshot.State == BackendConnectionState.Online;

    public bool IsUserOperationRunning
    {
        get => _isUserOperationRunning;
        private set => SetProperty(ref _isUserOperationRunning, value);
    }

    internal async Task RunUserOperationAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(operation);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdownSource.Token);
        IBackendSession? operationSession = null;
        bool entered = false;
        try
        {
            await _userOperationGate.WaitAsync(linked.Token).ConfigureAwait(false);
            entered = true;
            linked.Token.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            operationSession = ActiveSession;
            await _dispatcher.InvokeAsync(
                () =>
                {
                    IsUserOperationRunning = true;
                    LastUserOperationErrorMessage = null;
                },
                CancellationToken.None)
                .ConfigureAwait(false);
            await operation(linked.Token).ConfigureAwait(false);
        }
        catch (ClashAuthenticationException exception)
        {
            if (operationSession is not null &&
                ReferenceEquals(ActiveSession, operationSession))
            {
                await ReportFailureAsync(
                    operationSession,
                    "controller request",
                    exception).ConfigureAwait(false);
            }

            await _dispatcher.InvokeAsync(
                () => LastUserOperationErrorMessage = exception.Message,
                CancellationToken.None).ConfigureAwait(false);

            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await _dispatcher.InvokeAsync(
                () => LastUserOperationErrorMessage = exception.Message,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (entered)
            {
                try
                {
                    await _dispatcher.InvokeAsync(
                        () => IsUserOperationRunning = false,
                        CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch when (_shutdownSource.IsCancellationRequested)
                {
                }
                finally
                {
                    _userOperationGate.Release();
                }
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsInitialized)
            {
                return;
            }

            await _profileWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                BackendProfileSet loaded;
                try
                {
                    loaded = await _profileStore.LoadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    await _dispatcher.InvokeAsync(() =>
                    {
                        LastErrorMessage = $"Backend profiles could not be loaded: {exception.Message}";
                        IsInitialized = true;
                    }, CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                HashSet<Guid> profilesWithCredentials = [];
                foreach (BackendProfile profile in loaded.Profiles)
                {
                    if (await _credentialStore.ExistsAsync(profile.Id, cancellationToken)
                        .ConfigureAwait(false))
                    {
                        profilesWithCredentials.Add(profile.Id);
                    }
                }

                SetProfileState(loaded, profilesWithCredentials);
                await _dispatcher.InvokeAsync(() =>
                {
                    ReplaceCollection(_profiles, loaded.Profiles);
                    IsInitialized = true;
                }, cancellationToken).ConfigureAwait(false);

                if (loaded.ActiveProfileId is Guid activeProfileId)
                {
                    BackendProfile? activeProfile = loaded.Profiles.FirstOrDefault(
                        profile => profile.Id == activeProfileId);
                    if (activeProfile is not null)
                    {
                        await SwitchSessionAsync(activeProfile, _shutdownSource.Token)
                            .ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _profileWriteGate.Release();
            }
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public async Task<Guid> SaveAndConnectAsync(
        BackendSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);

        ValidateCredentialUpdate(request);
        if (request.ProfileId == Guid.Empty)
        {
            throw new ArgumentException(
                "The backend profile identifier must not be empty.",
                nameof(request));
        }

        BackendEndpoint endpoint = BackendEndpoint.Create(request.ControllerUri.AbsoluteUri);
        string name = string.IsNullOrWhiteSpace(request.Name)
            ? request.ControllerUri.Host
            : request.Name.Trim();
        Guid profileId = request.ProfileId ?? Guid.NewGuid();
        BackendProfile profile = new(profileId, name, endpoint);
        Guid? supersededProfileId = null;
        Exception? credentialCleanupException = null;

        await _profileWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BackendProfileSet current = GetProfileSet();
            List<BackendProfile> profiles = current.Profiles.ToList();
            int existingIndex = profiles.FindIndex(candidate => candidate.Id == profile.Id);
            if (existingIndex >= 0)
            {
                BackendProfile previous = profiles[existingIndex];
                bool endpointChanged = !previous.Endpoint.Equals(endpoint);
                if (endpointChanged &&
                    request.CredentialUpdate == BackendCredentialUpdate.Keep)
                {
                    throw new InvalidOperationException(
                        "The controller address changed. Replace or remove the saved secret before connecting.");
                }

                profile = new BackendProfile(
                    endpointChanged ? Guid.NewGuid() : profile.Id,
                    name,
                    endpoint,
                    previous.DisableCoreUpgrade,
                    previous.DisableTunMode);
                if (endpointChanged)
                {
                    supersededProfileId = previous.Id;
                }

                profiles[existingIndex] = profile;
            }
            else
            {
                if (request.ProfileId.HasValue)
                {
                    throw new KeyNotFoundException(
                        $"Backend profile '{request.ProfileId.Value}' does not exist.");
                }

                if (request.CredentialUpdate == BackendCredentialUpdate.Keep)
                {
                    throw new InvalidOperationException(
                        "A new backend cannot keep a previously saved secret.");
                }

                profiles.Add(profile);
            }

            bool shouldDeleteCredential = existingIndex >= 0 &&
                supersededProfileId is null &&
                request.CredentialUpdate == BackendCredentialUpdate.Remove;
            bool shouldSetCredential =
                request.CredentialUpdate == BackendCredentialUpdate.Replace;

            BackendProfileSet updated = new()
            {
                Profiles = profiles,
                ActiveProfileId = profile.Id,
            };

            bool preparedReplacementCredential = false;
            if (supersededProfileId.HasValue && shouldSetCredential)
            {
                await _credentialStore.SetAsync(
                    profile.Id,
                    new BackendCredential(request.Secret),
                    cancellationToken).ConfigureAwait(false);
                preparedReplacementCredential = true;
            }

            try
            {
                await _profileStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception profileCommitException)
            {
                if (preparedReplacementCredential)
                {
                    try
                    {
                        await _credentialStore.DeleteAsync(profile.Id, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception cleanupException)
                    {
                        throw new AggregateException(
                            "The backend update failed and its prepared credential could not be removed.",
                            profileCommitException,
                            cleanupException);
                    }
                }

                throw;
            }

            if (supersededProfileId is null)
            {
                try
                {
                    if (shouldDeleteCredential)
                    {
                        await _credentialStore.DeleteAsync(profile.Id, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else if (shouldSetCredential)
                    {
                        await _credentialStore.SetAsync(
                            profile.Id,
                            new BackendCredential(request.Secret),
                            cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception commitException)
                {
                    try
                    {
                        await _profileStore.SaveAsync(current, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception rollbackException)
                    {
                        throw new AggregateException(
                            "The backend update failed and its profile rollback also failed.",
                            commitException,
                            rollbackException);
                    }

                    throw;
                }
            }
            else
            {
                try
                {
                    await _credentialStore.DeleteAsync(
                        supersededProfileId.Value,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    credentialCleanupException = exception;
                }

                SetCredentialPresence(supersededProfileId.Value, hasCredential: false);
                SetCredentialPresence(profile.Id, hasCredential: shouldSetCredential);
            }

            if (supersededProfileId is null)
            {
                if (shouldDeleteCredential)
                {
                    SetCredentialPresence(profile.Id, hasCredential: false);
                }
                else if (shouldSetCredential)
                {
                    SetCredentialPresence(profile.Id, hasCredential: true);
                }
            }

            SetProfileSet(updated);
            await _dispatcher.InvokeAsync(
                () => ReplaceCollection(_profiles, updated.Profiles),
                CancellationToken.None).ConfigureAwait(false);
            await SwitchSessionAsync(profile, _shutdownSource.Token).ConfigureAwait(false);
            if (credentialCleanupException is not null)
            {
                await _dispatcher.InvokeAsync(
                    () => LastErrorMessage =
                        "The backend was updated, but its previous encrypted credential " +
                        $"could not be removed: {credentialCleanupException.Message}",
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _profileWriteGate.Release();
        }

        return profile.Id;
    }

    public async Task ActivateProfileAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _profileWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BackendProfile profile = FindProfile(profileId)
                ?? throw new KeyNotFoundException(
                    $"Backend profile '{profileId}' does not exist.");
            BackendProfileSet current = GetProfileSet();
            if (current.ActiveProfileId == profile.Id && ActiveProfile?.Id == profile.Id)
            {
                return;
            }

            BackendProfileSet updated = current with { ActiveProfileId = profile.Id };
            await _profileStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            SetProfileSet(updated);
            await SwitchSessionAsync(profile, _shutdownSource.Token).ConfigureAwait(false);
        }
        finally
        {
            _profileWriteGate.Release();
        }

    }

    public async Task RemoveProfileAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        bool removedActiveProfile;
        BackendProfileSet updated;

        await _profileWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BackendProfileSet current = GetProfileSet();
            List<BackendProfile> profiles = current.Profiles
                .Where(profile => profile.Id != profileId)
                .ToList();
            if (profiles.Count == current.Profiles.Count)
            {
                return;
            }

            removedActiveProfile = current.ActiveProfileId == profileId;
            updated = new BackendProfileSet
            {
                Profiles = profiles,
                ActiveProfileId = removedActiveProfile ? null : current.ActiveProfileId,
            };

            await _profileStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            try
            {
                await _credentialStore.DeleteAsync(profileId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception commitException)
            {
                try
                {
                    await _profileStore.SaveAsync(current, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception rollbackException)
                {
                    throw new AggregateException(
                        "The backend removal failed and its profile rollback also failed.",
                        commitException,
                        rollbackException);
                }

                throw;
            }
            SetCredentialPresence(profileId, hasCredential: false);
            SetProfileSet(updated);
            await _dispatcher.InvokeAsync(
                () => ReplaceCollection(_profiles, updated.Profiles),
                CancellationToken.None).ConfigureAwait(false);
            if (removedActiveProfile)
            {
                await SwitchSessionAsync(null, _shutdownSource.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            _profileWriteGate.Release();
        }

    }

    public Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        BackendProfile? profile = ActiveProfile;
        return profile is null
            ? Task.CompletedTask
            : SwitchSessionAsync(profile, cancellationToken);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        IBackendSession session = GetRequiredSession();

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DispatchForEpochAsync(session.Epoch, () =>
            {
                IsRefreshing = true;
                LastErrorMessage = null;
            }).ConfigureAwait(false);

            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                session.Lifetime);
            await LoadInitialStateAsync(session, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            await DispatchForEpochAsync(session.Epoch, () => IsRefreshing = false)
                .ConfigureAwait(false);
            _refreshGate.Release();
        }
    }

    public async Task SelectProxyAsync(
        string groupName,
        string proxyName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);
        ArgumentException.ThrowIfNullOrWhiteSpace(proxyName);
        if (ProxyCatalog?.Proxies.TryGetValue(groupName, out ClashProxy? group) == true &&
            group is not null &&
            !group.AllowsManualSelection)
        {
            throw new NotSupportedException(
                $"Proxy group '{groupName}' does not allow manual selection.");
        }

        IBackendSession session = GetRequiredSession();
        using CancellationTokenSource linked = CreateOperationSource(session, cancellationToken);
        await session.RestClient.SelectProxyAsync(groupName, proxyName, linked.Token)
            .ConfigureAwait(false);
        await RefreshProxiesAsync(session, linked.Token).ConfigureAwait(false);
    }

    public async Task TestProxyGroupAsync(
        string groupName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);
        IBackendSession session = GetRequiredSession();
        using CancellationTokenSource linked = CreateOperationSource(session, cancellationToken);
        ProxyDelayRequest request = new(
            ResolveDelayTarget(groupName, groupName, string.Empty),
            _settings.DelayTimeout);
        _ = await session.RestClient.MeasureProxyGroupDelayAsync(groupName, request, linked.Token)
            .ConfigureAwait(false);
        await RefreshProxiesAsync(session, linked.Token).ConfigureAwait(false);
    }

    public async Task TestProxyAsync(
        string groupName,
        string proxyName,
        string? providerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);
        ArgumentException.ThrowIfNullOrWhiteSpace(proxyName);
        IBackendSession session = GetRequiredSession();
        using CancellationTokenSource linked = CreateOperationSource(session, cancellationToken);
        string normalizedProvider = providerName?.Trim() ?? string.Empty;
        ProxyDelayRequest request = new(
            ResolveDelayTarget(groupName, proxyName, normalizedProvider),
            _settings.DelayTimeout);

        if (normalizedProvider.Length > 0)
        {
            EnsureCapabilityAvailable(session, ClashCapability.ProviderProxyHealthCheck);
            _ = await session.RestClient.MeasureProviderProxyDelayAsync(
                normalizedProvider,
                proxyName,
                request,
                linked.Token).ConfigureAwait(false);
        }
        else
        {
            _ = await session.RestClient.MeasureProxyDelayAsync(proxyName, request, linked.Token)
                .ConfigureAwait(false);
        }

        await RefreshProxiesAsync(session, linked.Token).ConfigureAwait(false);
        await PublishSuccessfulContactAsync(session).ConfigureAwait(false);
    }

    public async Task ClearFixedProxyAsync(
        string groupName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);
        IBackendSession session = GetRequiredSession();
        using CancellationTokenSource linked = CreateOperationSource(session, cancellationToken);
        await session.RestClient.ClearFixedProxyAsync(groupName, linked.Token).ConfigureAwait(false);
        await RefreshProxiesAsync(session, linked.Token).ConfigureAwait(false);
        await PublishSuccessfulContactAsync(session).ConfigureAwait(false);
    }

    public Task RefreshSmartWeightsAsync(CancellationToken cancellationToken = default)
    {
        IBackendSession session = GetRequiredSession();
        return RefreshSmartWeightsAsync(session, cancellationToken);
    }

    public async Task ResetSmartWeightsAsync(CancellationToken cancellationToken = default)
    {
        IBackendSession session = GetRequiredSession();
        EnsureCapabilityAvailable(session, ClashCapability.SmartWeightReset);
        using CancellationTokenSource linked = CreateOperationSource(session, cancellationToken);
        await session.RestClient.FlushSmartWeightsAsync(linked.Token).ConfigureAwait(false);
        await DispatchForEpochAsync(session.Epoch, () => SmartWeights = null)
            .ConfigureAwait(false);
        await TryRefreshSmartWeightsAsync(session, linked.Token).ConfigureAwait(false);
        await PublishSuccessfulContactAsync(session).ConfigureAwait(false);
    }

    public async Task TestAllProxyGroupsAsync(CancellationToken cancellationToken = default)
    {
        IBackendSession session = GetRequiredSession();
        using CancellationTokenSource linked = CreateOperationSource(session, cancellationToken);
        ProxyCatalog catalog = ProxyCatalog ?? await session.RestClient.GetProxiesAsync(linked.Token)
            .ConfigureAwait(false);

        foreach (ClashProxy group in catalog.Proxies.Values.Where(static proxy => proxy.All.Count > 0))
        {
            linked.Token.ThrowIfCancellationRequested();
            ProxyDelayRequest request = new(
                group.TestUrl ?? _settings.DelayTestUri,
                _settings.DelayTimeout);
            _ = await session.RestClient.MeasureProxyGroupDelayAsync(group.Name, request, linked.Token)
                .ConfigureAwait(false);
        }

        await RefreshProxiesAsync(session, linked.Token).ConfigureAwait(false);
    }

    public async Task UpdateAllProxyProvidersAsync(CancellationToken cancellationToken = default)
    {
        IBackendSession session = GetRequiredSession();
        EnsureCapabilityAvailable(session, ClashCapability.ProviderProxyUpdate);
        using CancellationTokenSource linked = CreateOperationSource(session, cancellationToken);
        ProxyProviderCatalog providers = ProxyProviders
            ?? await session.RestClient.GetProxyProvidersAsync(linked.Token).ConfigureAwait(false);

        foreach (ClashProxyProvider provider in providers.Providers.Values
            .Where(static provider => CanManageProxyProvider(provider, requireUpdate: true))
            .OrderBy(static provider => provider.Name, StringComparer.Ordinal))
        {
            linked.Token.ThrowIfCancellationRequested();
            await session.RestClient.UpdateProxyProviderAsync(provider.Name, linked.Token)
                .ConfigureAwait(false);
        }

        await RefreshProxiesAsync(session, linked.Token).ConfigureAwait(false);
        await PublishSuccessfulContactAsync(session).ConfigureAwait(false);
    }

    public async Task UpdateProxyProviderAsync(
        string providerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        IBackendSession session = GetRequiredSession();
        EnsureCapabilityAvailable(session, ClashCapability.ProviderProxyUpdate);
        using CancellationTokenSource linked = CreateOperationSource(session, cancellationToken);
        await session.RestClient.UpdateProxyProviderAsync(providerName, linked.Token)
            .ConfigureAwait(false);
        await RefreshProxiesAsync(session, linked.Token).ConfigureAwait(false);
        await PublishSuccessfulContactAsync(session).ConfigureAwait(false);
    }

    public async Task CheckProxyProviderAsync(
        string providerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        IBackendSession session = GetRequiredSession();
        EnsureCapabilityAvailable(session, ClashCapability.ProviderProxyHealthCheck);
        using CancellationTokenSource linked = CreateOperationSource(session, cancellationToken);
        await session.RestClient.CheckProxyProviderAsync(providerName, linked.Token)
            .ConfigureAwait(false);
        await RefreshProxiesAsync(session, linked.Token).ConfigureAwait(false);
        await PublishSuccessfulContactAsync(session).ConfigureAwait(false);
    }

    public async Task CheckAllProxyProvidersAsync(CancellationToken cancellationToken = default)
    {
        IBackendSession session = GetRequiredSession();
        EnsureCapabilityAvailable(session, ClashCapability.ProviderProxyHealthCheck);
        using CancellationTokenSource linked = CreateOperationSource(session, cancellationToken);
        ProxyProviderCatalog providers = ProxyProviders
            ?? await session.RestClient.GetProxyProvidersAsync(linked.Token).ConfigureAwait(false);

        foreach (ClashProxyProvider provider in providers.Providers.Values
            .Where(static provider => CanManageProxyProvider(provider, requireUpdate: false))
            .OrderBy(static provider => provider.Name, StringComparer.Ordinal))
        {
            linked.Token.ThrowIfCancellationRequested();
            await session.RestClient.CheckProxyProviderAsync(provider.Name, linked.Token)
                .ConfigureAwait(false);
        }

        await RefreshProxiesAsync(session, linked.Token).ConfigureAwait(false);
        await PublishSuccessfulContactAsync(session).ConfigureAwait(false);
    }

    public async Task CloseConnectionAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        IBackendSession session = GetRequiredSession();
        using CancellationTokenSource linked = CreateOperationSource(session, cancellationToken);
        await session.RestClient.CloseConnectionAsync(connectionId, linked.Token).ConfigureAwait(false);

        await DispatchForEpochAsync(session.Epoch, () =>
        {
            if (ConnectionSnapshot is not ConnectionStreamSnapshot current)
            {
                return;
            }

            ConnectionSnapshot = current with
            {
                Connections = current.Connections
                    .Where(connection => !string.Equals(
                        connection.Id,
                        connectionId,
                        StringComparison.Ordinal))
                    .ToArray(),
            };
        }).ConfigureAwait(false);
    }

    public async Task CloseAllConnectionsAsync(CancellationToken cancellationToken = default)
    {
        IBackendSession session = GetRequiredSession();
        using CancellationTokenSource linked = CreateOperationSource(session, cancellationToken);
        await session.RestClient.CloseAllConnectionsAsync(linked.Token).ConfigureAwait(false);
        await DispatchForEpochAsync(session.Epoch, () =>
        {
            if (ConnectionSnapshot is ConnectionStreamSnapshot current)
            {
                ConnectionSnapshot = current with { Connections = [] };
            }
        }).ConfigureAwait(false);
    }

    public async Task BlockSmartConnectionAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        IBackendSession session = GetRequiredSession();
        EnsureCapabilityAvailable(session, ClashCapability.SmartConnectionBlock);
        using CancellationTokenSource linked = CreateOperationSource(session, cancellationToken);
        await session.RestClient.BlockSmartConnectionAsync(connectionId, linked.Token)
            .ConfigureAwait(false);
        await PublishSuccessfulContactAsync(session).ConfigureAwait(false);
    }

    public Task RefreshHonkRuntimeStatisticsAsync(
        CancellationToken cancellationToken = default)
    {
        IBackendSession session = GetRequiredSession();
        return RefreshHonkRuntimeStatisticsAsync(session, cancellationToken);
    }

    public async Task RefreshRulesAsync(CancellationToken cancellationToken = default)
    {
        IBackendSession session = GetRequiredSession();
        using CancellationTokenSource linked = CreateOperationSource(session, cancellationToken);
        await RefreshRulesAsync(session, linked.Token).ConfigureAwait(false);
    }

    public async Task UpdateRuleProviderAsync(
        string providerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        IBackendSession session = GetRequiredSession();
        using CancellationTokenSource linked = CreateOperationSource(session, cancellationToken);
        await session.RestClient.UpdateRuleProviderAsync(providerName, linked.Token)
            .ConfigureAwait(false);
        await RefreshRulesAsync(session, linked.Token).ConfigureAwait(false);
        await PublishSuccessfulContactAsync(session).ConfigureAwait(false);
    }

    public async Task UpdateAllRuleProvidersAsync(CancellationToken cancellationToken = default)
    {
        IBackendSession session = GetRequiredSession();
        using CancellationTokenSource linked = CreateOperationSource(session, cancellationToken);
        RuleProviderCatalog providers = RuleProviders
            ?? await session.RestClient.GetRuleProvidersAsync(linked.Token).ConfigureAwait(false);

        foreach (ClashRuleProvider provider in providers.Providers.Values
            .Where(static provider => !provider.VehicleType.Equals(
                "Inline",
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(static provider => provider.Name, StringComparer.Ordinal))
        {
            linked.Token.ThrowIfCancellationRequested();
            await session.RestClient.UpdateRuleProviderAsync(provider.Name, linked.Token)
                .ConfigureAwait(false);
        }

        await RefreshRulesAsync(session, linked.Token).ConfigureAwait(false);
        await PublishSuccessfulContactAsync(session).ConfigureAwait(false);
    }

    public async Task SetRuleDisabledAsync(
        int? index,
        string? identifier,
        bool disabled,
        CancellationToken cancellationToken = default)
    {
        IBackendSession session = GetRequiredSession();
        using CancellationTokenSource linked = CreateOperationSource(session, cancellationToken);
        CapabilitySupport identifierSupport = session.Capabilities
            .GetObservation(ClashCapability.RuleDisableByIdentifier).Support;
        CapabilitySupport indexSupport = session.Capabilities
            .GetObservation(ClashCapability.RuleDisableByIndex).Support;

        if (!string.IsNullOrWhiteSpace(identifier) &&
            identifierSupport != CapabilitySupport.Unsupported)
        {
            await session.RestClient.ToggleRuleDisabledByIdentifierAsync(identifier, linked.Token)
                .ConfigureAwait(false);
        }
        else if (index.HasValue && indexSupport != CapabilitySupport.Unsupported)
        {
            await session.RestClient.SetRuleDisabledStatesAsync(
                new Dictionary<int, bool> { [index.Value] = disabled },
                linked.Token).ConfigureAwait(false);
        }
        else
        {
            throw new NotSupportedException(
                "This backend does not expose a supported rule disable operation for the selected rule.");
        }

        await RefreshRulesAsync(session, linked.Token).ConfigureAwait(false);
        await PublishSuccessfulContactAsync(session).ConfigureAwait(false);
    }

    public Task SetModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        return PatchConfigurationAsync(
            new ClashConfigurationPatch { Mode = mode.Trim() },
            cancellationToken);
    }

    public Task SetTunEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        IBackendSession session = GetRequiredSession();
        if (session.Profile.DisableTunMode)
        {
            throw new InvalidOperationException("TUN control is disabled for this backend profile.");
        }

        if (Configuration?.Tun is null)
        {
            throw new NotSupportedException("The active backend does not expose TUN configuration.");
        }

        return PatchConfigurationAsync(
            new ClashConfigurationPatch { TunEnabled = enabled },
            cancellationToken);
    }

    public async Task PatchConfigurationAsync(
        ClashConfigurationPatch patch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ValidateConfigurationPatch(patch);
        IBackendSession session = GetRequiredSession();
        if (patch.TunEnabled.HasValue && session.Profile.DisableTunMode)
        {
            throw new InvalidOperationException("TUN control is disabled for this backend profile.");
        }

        EnsureCapabilityAvailable(session, ClashCapability.ConfigurationPatch);
        using CancellationTokenSource linked = CreateOperationSource(session, cancellationToken);
        await session.RestClient.PatchConfigurationAsync(patch, linked.Token).ConfigureAwait(false);
        await RefreshConfigurationAsync(session, linked.Token).ConfigureAwait(false);
        await PublishSuccessfulContactAsync(session).ConfigureAwait(false);
    }

    public async Task ReloadConfigurationAsync(CancellationToken cancellationToken = default)
    {
        IBackendSession session = GetRequiredSession();
        EnsureCapabilityAvailable(session, ClashCapability.ConfigurationReload);
        using CancellationTokenSource linked = CreateOperationSource(session, cancellationToken);
        await session.RestClient.ReloadConfigurationAsync(linked.Token).ConfigureAwait(false);
        await LoadInitialStateAsync(session, linked.Token).ConfigureAwait(false);
        await PublishSuccessfulContactAsync(session).ConfigureAwait(false);
    }

    public async Task UpdateConfigurationAsync(
        string? path,
        string? payload,
        bool force,
        CancellationToken cancellationToken = default)
    {
        string normalizedPath = path?.Trim() ?? string.Empty;
        string normalizedPayload = payload ?? string.Empty;
        if (normalizedPath.Length == 0 && string.IsNullOrWhiteSpace(normalizedPayload))
        {
            throw new ArgumentException("Enter a controller path or configuration payload.");
        }

        IBackendSession session = GetRequiredSession();
        EnsureCapabilityAvailable(session, ClashCapability.ConfigurationUpdate);
        using CancellationTokenSource linked = CreateOperationSource(session, cancellationToken);
        await session.RestClient.UpdateConfigurationAsync(
            new ClashConfigurationUpdate
            {
                Path = normalizedPath,
                Payload = normalizedPayload,
                Force = force,
            },
            linked.Token).ConfigureAwait(false);
        await LoadInitialStateAsync(session, linked.Token).ConfigureAwait(false);
        await PublishSuccessfulContactAsync(session).ConfigureAwait(false);
    }

    public async Task<DnsQueryResult> QueryDnsAsync(
        string name,
        string type,
        CancellationToken cancellationToken = default)
    {
        IBackendSession session = GetRequiredSession();
        using CancellationTokenSource linked = CreateOperationSource(session, cancellationToken);
        DnsQueryResult result = await session.RestClient.QueryDnsAsync(
            new DnsQueryRequest(name, type),
            linked.Token).ConfigureAwait(false);
        await PublishSuccessfulContactAsync(session).ConfigureAwait(false);
        return result;
    }

    public async Task FlushDnsCacheAsync(CancellationToken cancellationToken = default)
    {
        IBackendSession session = GetRequiredSession();
        using CancellationTokenSource linked = CreateOperationSource(session, cancellationToken);
        await session.RestClient.FlushDnsCacheAsync(linked.Token).ConfigureAwait(false);
        await PublishSuccessfulContactAsync(session).ConfigureAwait(false);
    }

    public async Task FlushFakeIpCacheAsync(CancellationToken cancellationToken = default)
    {
        IBackendSession session = GetRequiredSession();
        using CancellationTokenSource linked = CreateOperationSource(session, cancellationToken);
        await session.RestClient.FlushFakeIpCacheAsync(linked.Token).ConfigureAwait(false);
        await PublishSuccessfulContactAsync(session).ConfigureAwait(false);
    }

    public async Task UpdateGeoDataAsync(CancellationToken cancellationToken = default)
    {
        IBackendSession session = GetRequiredSession();
        EnsureCapabilityAvailable(session, ClashCapability.GeoDataUpdate);
        using CancellationTokenSource linked = CreateOperationSource(session, cancellationToken);
        await session.RestClient.UpdateGeoDataAsync(linked.Token).ConfigureAwait(false);
        await PublishSuccessfulContactAsync(session).ConfigureAwait(false);
    }

    public async Task RestartCoreAsync(CancellationToken cancellationToken = default)
    {
        IBackendSession session = GetRequiredSession();
        EnsureCapabilityAvailable(session, ClashCapability.CoreRestart);
        using CancellationTokenSource linked = CreateOperationSource(session, cancellationToken);
        await session.RestClient.RestartCoreAsync(linked.Token).ConfigureAwait(false);
        await PublishSuccessfulContactAsync(session).ConfigureAwait(false);
        await ReconnectAfterCoreOperationAsync(session, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpgradeCoreAsync(
        CoreUpgradeChannel channel,
        CancellationToken cancellationToken = default)
    {
        IBackendSession session = GetRequiredSession();
        if (session.Profile.DisableCoreUpgrade)
        {
            throw new InvalidOperationException("Core upgrades are disabled for this backend profile.");
        }

        EnsureCapabilityAvailable(session, ClashCapability.CoreUpgrade);
        using CancellationTokenSource linked = CreateOperationSource(session, cancellationToken);
        await session.RestClient.UpgradeCoreAsync(channel, linked.Token).ConfigureAwait(false);
        await PublishSuccessfulContactAsync(session).ConfigureAwait(false);
        await ReconnectAfterCoreOperationAsync(session, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReprobeCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        IBackendSession session = GetRequiredSession();
        session.Capabilities.Reset();
        await DispatchForEpochAsync(session.Epoch, () =>
        {
            SessionSnapshot = SessionSnapshot with
            {
                Capabilities = session.Capabilities.GetSnapshot(),
            };
        }).ConfigureAwait(false);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public void ClearCapabilityCache()
    {
        ThrowIfDisposed();
        IBackendSession? session = ActiveSession;
        if (session is null)
        {
            return;
        }

        session.Capabilities.Reset();
        SessionSnapshot = SessionSnapshot with
        {
            Capabilities = session.Capabilities.GetSnapshot(),
        };
    }

    public void ClearLogs()
    {
        ThrowIfDisposed();
        if (!_dispatcher.HasThreadAccess)
        {
            throw new InvalidOperationException("The log buffer must be cleared on the UI thread.");
        }

        lock (_logIterationGate)
        {
            _ = Interlocked.Increment(ref _logGeneration);
            _logIterationSource?.Cancel();
        }
        int removedCount = _logs.Count;
        _logs.Clear();
        LogsChanged?.Invoke(
            this,
            new SessionLogsChangedEventArgs([], removedCount, isReset: true));
    }

    private async Task SwitchSessionAsync(
        BackendProfile? profile,
        CancellationToken cancellationToken)
    {
        await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            BackendProfileSet currentProfiles = GetProfileSet();
            BackendProfile? currentProfile = profile is null
                ? null
                : currentProfiles.Profiles.FirstOrDefault(candidate => candidate.Id == profile.Id);
            if ((profile is null && currentProfiles.ActiveProfileId is not null) ||
                (profile is not null &&
                    (currentProfiles.ActiveProfileId != profile.Id || currentProfile is null)))
            {
                return;
            }

            profile = currentProfile;

            SessionEpoch epoch = new(Interlocked.Increment(ref _epochValue));
            Exception? cleanupFailure = null;
            try
            {
                await StopCurrentSessionAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }

            await _dispatcher.InvokeAsync(
                () => ResetSessionState(epoch, profile),
                CancellationToken.None)
                .ConfigureAwait(false);

            if (profile is null)
            {
                if (cleanupFailure is not null)
                {
                    await PublishSessionCleanupFailureAsync(cleanupFailure)
                        .ConfigureAwait(false);
                }

                return;
            }

            IBackendSession session;
            try
            {
                BackendCredential credential = await _credentialStore.GetAsync(
                    profile.Id,
                    cancellationToken)
                    .ConfigureAwait(false)
                    ?? new BackendCredential(string.Empty);
                session = await _sessionFactory.CreateAsync(
                    profile,
                    credential,
                    epoch,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await PublishCanceledSessionSwitchAsync(epoch, profile.Id).ConfigureAwait(false);
                throw;
            }
            catch (Exception exception)
            {
                string detail = cleanupFailure is null
                    ? exception.Message
                    : $"{exception.Message} Previous session cleanup also failed: " +
                        cleanupFailure.Message;
                await _dispatcher.InvokeAsync(() =>
                {
                    if (Epoch != epoch)
                    {
                        return;
                    }

                    LastErrorMessage = detail;
                    SessionSnapshot = new BackendSessionSnapshot
                    {
                        Epoch = epoch,
                        ProfileId = profile.Id,
                        State = BackendConnectionState.OfflineRetrying,
                        StateChangedAt = _timeProvider.GetUtcNow(),
                        StatusDetail = detail,
                    };
                }, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            if (cancellationToken.IsCancellationRequested || Epoch != epoch)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                await PublishCanceledSessionSwitchAsync(epoch, profile.Id).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            Volatile.Write(ref _activeSession, session);
            session.StreamClient.StreamStatusChanged += OnStreamStatusChanged;
            session.StreamClient.StreamItemsDropped += OnStreamItemsDropped;
            session.Capabilities.Changed += OnCapabilityChanged;
            CancellationTokenSource workSource = CancellationTokenSource.CreateLinkedTokenSource(
                session.Lifetime);
            _sessionWorkSource = workSource;

            await DispatchForEpochAsync(epoch, () =>
            {
                SessionSnapshot = session.Snapshot;
                LastErrorMessage = session.Snapshot.StatusDetail;
                OnPropertyChanged(nameof(HasActiveSession));
            }).ConfigureAwait(false);

            if (session.Snapshot.State != BackendConnectionState.Unauthorized)
            {
                List<Task> backgroundTasks =
                [
                    LoadInitialStateAsync(session, workSource.Token),
                    PumpConnectionsAsync(session, workSource.Token),
                    PumpLogsAsync(session, workSource.Token),
                    PumpTrafficAsync(session, workSource.Token),
                    PumpMemoryAsync(session, workSource.Token),
                ];
                backgroundTasks.Add(PumpHonkRuntimeStatisticsAsync(
                    session,
                    workSource.Token));

                _backgroundTasks = backgroundTasks.ToArray();
            }

            if (cleanupFailure is not null)
            {
                await PublishSessionCleanupFailureAsync(cleanupFailure).ConfigureAwait(false);
            }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task StopCurrentSessionAsync()
    {
        CancellationTokenSource? workSource = _sessionWorkSource;
        _sessionWorkSource = null;
        Task[] tasks = _backgroundTasks;
        _backgroundTasks = [];
        IBackendSession? session = Interlocked.Exchange(ref _activeSession, null);
        List<Exception> cleanupFailures = [];

        if (session is not null)
        {
            session.StreamClient.StreamStatusChanged -= OnStreamStatusChanged;
            session.StreamClient.StreamItemsDropped -= OnStreamItemsDropped;
            session.Capabilities.Changed -= OnCapabilityChanged;
        }

        if (workSource is not null)
        {
            try
            {
                await workSource.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }
        }

        if (session is not null)
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }
        }

        if (tasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                // Stream failures are surfaced when their individual tasks terminate.
            }
        }

        try
        {
            workSource?.Dispose();
        }
        catch (Exception exception)
        {
            cleanupFailures.Add(exception);
        }

        if (cleanupFailures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(cleanupFailures[0]).Throw();
        }

        if (cleanupFailures.Count > 1)
        {
            throw new AggregateException("One or more session cleanup operations failed.", cleanupFailures);
        }
    }

    private ValueTask PublishSessionCleanupFailureAsync(Exception exception) =>
        _dispatcher.InvokeAsync(() =>
            LastErrorMessage =
                $"The previous backend session could not be cleaned up completely: {exception.Message}",
            CancellationToken.None);

    private async Task PublishCanceledSessionSwitchAsync(
        SessionEpoch epoch,
        Guid profileId)
    {
        if (_shutdownSource.IsCancellationRequested)
        {
            return;
        }

        await DispatchForEpochAsync(epoch, () =>
        {
            const string detail = "The connection attempt was canceled. Reconnect to try again.";
            LastErrorMessage = detail;
            SessionSnapshot = new BackendSessionSnapshot
            {
                Epoch = epoch,
                ProfileId = profileId,
                State = BackendConnectionState.OfflineRetrying,
                StateChangedAt = _timeProvider.GetUtcNow(),
                StatusDetail = detail,
            };
        }).ConfigureAwait(false);
    }

    private async Task LoadInitialStateAsync(
        IBackendSession session,
        CancellationToken cancellationToken)
    {
        Task<bool>[] loads =
        [
            LoadResourceAsync(
                session,
                "configuration",
                session.RestClient.GetConfigurationAsync,
                value => Configuration = value,
                cancellationToken),
            LoadResourceAsync(
                session,
                "proxies",
                session.RestClient.GetProxiesAsync,
                value => ProxyCatalog = value,
                cancellationToken),
            LoadResourceAsync(
                session,
                "proxy providers",
                session.RestClient.GetProxyProvidersAsync,
                value => ProxyProviders = value,
                cancellationToken),
            LoadResourceAsync(
                session,
                "rules",
                session.RestClient.GetRulesAsync,
                value => RuleCatalog = value,
                cancellationToken),
            LoadResourceAsync(
                session,
                "rule providers",
                session.RestClient.GetRuleProvidersAsync,
                value => RuleProviders = value,
                cancellationToken),
        ];

        bool[] results;
        try
        {
            results = await Task.WhenAll(loads).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        int successes = results.Count(static succeeded => succeeded);
        if (successes == 0)
        {
            return;
        }

        await DispatchForEpochAsync(session.Epoch, () =>
        {
            BackendConnectionState state = successes == results.Length
                ? BackendConnectionState.Online
                : BackendConnectionState.Degraded;
            BackendConnectionState? streamState = GetStreamConnectionState();
            if (streamState is
                BackendConnectionState.Unauthorized or
                BackendConnectionState.Degraded or
                BackendConnectionState.OfflineRetrying)
            {
                state = streamState.Value;
            }

            if (SessionSnapshot.State == BackendConnectionState.Unauthorized)
            {
                return;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            SessionSnapshot = SessionSnapshot with
            {
                State = state,
                StateChangedAt = SessionSnapshot.State == state
                    ? SessionSnapshot.StateChangedAt
                    : now,
                LastSuccessfulContactAt = now,
                StatusDetail = state == BackendConnectionState.Online
                    ? null
                    : SessionSnapshot.StatusDetail,
                Capabilities = session.Capabilities.GetSnapshot(),
            };
            if (state == BackendConnectionState.Online)
            {
                LastErrorMessage = null;
            }
        }).ConfigureAwait(false);

        await TryRefreshSmartWeightsAsync(session, cancellationToken).ConfigureAwait(false);
        await TryRefreshHonkRuntimeStatisticsAsync(session, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> LoadResourceAsync<T>(
        IBackendSession session,
        string operation,
        Func<CancellationToken, Task<T>> load,
        Action<T> publish,
        CancellationToken cancellationToken)
    {
        try
        {
            T value = await load(cancellationToken).ConfigureAwait(false);
            await DispatchForEpochAsync(session.Epoch, () =>
            {
                _resourceFailures.Remove(operation);
                publish(value);
            }).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            await ReportFailureAsync(
                session,
                operation,
                exception,
                isResourceFailure: true).ConfigureAwait(false);
            return false;
        }
    }

    private async Task RefreshProxiesAsync(
        IBackendSession session,
        CancellationToken cancellationToken)
    {
        ProxyCatalog proxies = await session.RestClient.GetProxiesAsync(cancellationToken)
            .ConfigureAwait(false);
        ProxyProviderCatalog providers = await session.RestClient.GetProxyProvidersAsync(cancellationToken)
            .ConfigureAwait(false);
        await DispatchForEpochAsync(session.Epoch, () =>
        {
            _resourceFailures.Remove("proxies");
            _resourceFailures.Remove("proxy providers");
            ProxyCatalog = proxies;
            ProxyProviders = providers;
            MarkLiveContact();
        }).ConfigureAwait(false);
        await TryRefreshSmartWeightsAsync(session, cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshSmartWeightsAsync(
        IBackendSession session,
        CancellationToken cancellationToken)
    {
        EnsureCapabilityAvailable(session, ClashCapability.SmartWeights);
        if (!ContainsSmartGroup(ProxyCatalog))
        {
            await DispatchForEpochAsync(session.Epoch, () => SmartWeights = null)
                .ConfigureAwait(false);
            return;
        }

        using CancellationTokenSource linked = CreateOperationSource(session, cancellationToken);
        SmartWeights weights = await session.RestClient.GetSmartWeightsAsync(linked.Token)
            .ConfigureAwait(false);
        await DispatchForEpochAsync(session.Epoch, () => SmartWeights = weights)
            .ConfigureAwait(false);
        await PublishSuccessfulContactAsync(session).ConfigureAwait(false);
    }

    private async Task TryRefreshSmartWeightsAsync(
        IBackendSession session,
        CancellationToken cancellationToken)
    {
        if (!ContainsSmartGroup(ProxyCatalog) ||
            session.Capabilities.GetObservation(ClashCapability.SmartWeights).Support ==
                CapabilitySupport.Unsupported)
        {
            await DispatchForEpochAsync(session.Epoch, () => SmartWeights = null)
                .ConfigureAwait(false);
            return;
        }

        try
        {
            await RefreshSmartWeightsAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ClashAuthenticationException exception)
        {
            await ReportFailureAsync(session, "smart weights", exception).ConfigureAwait(false);
        }
        catch
        {
            // Smart groups are response-driven; failure of this optional detail must not fail proxy refresh.
            if (session.Capabilities.GetObservation(ClashCapability.SmartWeights).Support ==
                CapabilitySupport.Unsupported)
            {
                await DispatchForEpochAsync(session.Epoch, () => SmartWeights = null)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task RefreshHonkRuntimeStatisticsAsync(
        IBackendSession session,
        CancellationToken cancellationToken)
    {
        EnsureCapabilityAvailable(session, ClashCapability.RuntimeStatistics);
        using CancellationTokenSource linked = CreateOperationSource(session, cancellationToken);
        HonkRuntimeStatistics statistics = await session.RestClient
            .GetHonkRuntimeStatisticsAsync(linked.Token).ConfigureAwait(false);
        await DispatchForEpochAsync(
            session.Epoch,
            () => HonkRuntimeStatistics = statistics).ConfigureAwait(false);
        await PublishSuccessfulContactAsync(session).ConfigureAwait(false);
    }

    private async Task TryRefreshHonkRuntimeStatisticsAsync(
        IBackendSession session,
        CancellationToken cancellationToken)
    {
        if (session.Capabilities.GetObservation(ClashCapability.RuntimeStatistics).Support ==
            CapabilitySupport.Unsupported)
        {
            await DispatchForEpochAsync(session.Epoch, () => HonkRuntimeStatistics = null)
                .ConfigureAwait(false);
            return;
        }

        try
        {
            await RefreshHonkRuntimeStatisticsAsync(session, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ClashAuthenticationException exception)
        {
            await ReportFailureAsync(
                session,
                "runtime statistics",
                exception).ConfigureAwait(false);
        }
        catch
        {
            // Runtime statistics are optional and polled separately from core session health.
            if (session.Capabilities.GetObservation(ClashCapability.RuntimeStatistics).Support ==
                CapabilitySupport.Unsupported)
            {
                await DispatchForEpochAsync(session.Epoch, () => HonkRuntimeStatistics = null)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task RefreshRulesAsync(
        IBackendSession session,
        CancellationToken cancellationToken)
    {
        RuleCatalog rules = await session.RestClient.GetRulesAsync(cancellationToken)
            .ConfigureAwait(false);
        RuleProviderCatalog providers = await session.RestClient.GetRuleProvidersAsync(cancellationToken)
            .ConfigureAwait(false);
        await DispatchForEpochAsync(session.Epoch, () =>
        {
            _resourceFailures.Remove("rules");
            _resourceFailures.Remove("rule providers");
            RuleCatalog = rules;
            RuleProviders = providers;
            MarkLiveContact();
        }).ConfigureAwait(false);
    }

    private async Task RefreshConfigurationAsync(
        IBackendSession session,
        CancellationToken cancellationToken)
    {
        ClashConfiguration configuration = await session.RestClient
            .GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        await DispatchForEpochAsync(
            session.Epoch,
            () =>
            {
                _resourceFailures.Remove("configuration");
                Configuration = configuration;
                MarkLiveContact();
            }).ConfigureAwait(false);
    }

    private async Task PublishSuccessfulContactAsync(IBackendSession session)
    {
        await DispatchForEpochAsync(session.Epoch, () =>
        {
            SessionSnapshot = SessionSnapshot with
            {
                LastSuccessfulContactAt = _timeProvider.GetUtcNow(),
                Capabilities = session.Capabilities.GetSnapshot(),
            };
        }).ConfigureAwait(false);
    }

    private async Task ReconnectAfterCoreOperationAsync(
        IBackendSession session,
        CancellationToken cancellationToken)
    {
        BackendProfile profile = session.Profile;
        await Task.Delay(TimeSpan.FromMilliseconds(750), _timeProvider, cancellationToken)
            .ConfigureAwait(false);
        if (Epoch == session.Epoch && ActiveProfile?.Id == profile.Id)
        {
            await SwitchSessionAsync(profile, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void EnsureCapabilityAvailable(
        IBackendSession session,
        ClashCapability capability)
    {
        if (session.Capabilities.GetObservation(capability).Support == CapabilitySupport.Unsupported)
        {
            throw new NotSupportedException(
                $"The active backend does not support {capability}.");
        }
    }

    private static void ValidateConfigurationPatch(ClashConfigurationPatch patch)
    {
        ValidatePort(patch.Port, nameof(patch.Port));
        ValidatePort(patch.SocksPort, nameof(patch.SocksPort));
        ValidatePort(patch.RedirPort, nameof(patch.RedirPort));
        ValidatePort(patch.TProxyPort, nameof(patch.TProxyPort));
        ValidatePort(patch.MixedPort, nameof(patch.MixedPort));

        if (patch.Port is null &&
            patch.SocksPort is null &&
            patch.RedirPort is null &&
            patch.TProxyPort is null &&
            patch.MixedPort is null &&
            patch.AllowLan is null &&
            patch.BindAddress is null &&
            patch.Mode is null &&
            patch.LogLevel is null &&
            patch.Ipv6 is null &&
            patch.TunEnabled is null)
        {
            throw new ArgumentException("The configuration patch does not contain any changes.", nameof(patch));
        }
    }

    private static void ValidatePort(int? port, string parameterName)
    {
        if (port is < 0 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                port,
                "Controller listener ports must be between 0 and 65535.");
        }
    }

    private static bool CanManageProxyProvider(
        ClashProxyProvider provider,
        bool requireUpdate)
    {
        if (provider.Name.Equals("default", StringComparison.OrdinalIgnoreCase) ||
            provider.VehicleType.Equals("Compatible", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !requireUpdate || !provider.VehicleType.Equals(
            "Inline",
            StringComparison.OrdinalIgnoreCase);
    }

    private Uri ResolveDelayTarget(
        string groupName,
        string proxyName,
        string providerName)
    {
        Uri? groupTarget = ProxyCatalog?.Proxies.TryGetValue(
            groupName,
            out ClashProxy? group) == true
            ? group.TestUrl
            : null;
        Uri? proxyTarget = ProxyCatalog?.Proxies.TryGetValue(
            proxyName,
            out ClashProxy? proxy) == true
            ? proxy.TestUrl
            : null;
        Uri? providerTarget = providerName.Length > 0 && ProxyProviders is not null
            ? ProxyProviders.Providers.Values.FirstOrDefault(provider => string.Equals(
                provider.Name,
                providerName,
                StringComparison.Ordinal))?.TestUrl
            : null;

        return groupTarget ?? providerTarget ?? proxyTarget ?? _settings.DelayTestUri;
    }

    private static bool ContainsSmartGroup(ProxyCatalog? catalog) =>
        catalog?.Proxies.Values.Any(static proxy =>
            proxy.Kind == ClashProxyKind.Smart ||
            proxy.Type.Equals("Smart", StringComparison.OrdinalIgnoreCase)) == true;

    private async Task PumpConnectionsAsync(
        IBackendSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            long lastPublishedAt = 0;
            long previousSampleAt = 0;
            Dictionary<string, ClashConnection> previousConnections = new(StringComparer.Ordinal);
            await foreach (ConnectionStreamSnapshot snapshot in session.StreamClient
                .StreamConnectionsAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                if (!ShouldPublish(ref lastPublishedAt, TimeSpan.FromMilliseconds(250)))
                {
                    continue;
                }

                long sampleAt = _timeProvider.GetTimestamp();
                Dictionary<string, ConnectionTransferRate> rates = new(StringComparer.Ordinal);
                if (previousSampleAt != 0)
                {
                    TimeSpan elapsed = _timeProvider.GetElapsedTime(previousSampleAt, sampleAt);
                    foreach (ClashConnection connection in snapshot.Connections)
                    {
                        previousConnections.TryGetValue(
                            connection.Id,
                            out ClashConnection? previous);
                        if (previous is not null)
                        {
                            rates[connection.Id] = ConnectionNormalizer.CalculateTransferRate(
                                connection,
                                previous,
                                elapsed);
                        }
                    }
                }

                previousSampleAt = sampleAt;
                Dictionary<string, ClashConnection> nextConnections = new(StringComparer.Ordinal);
                foreach (ClashConnection connection in snapshot.Connections)
                {
                    nextConnections[connection.Id] = connection;
                }

                previousConnections = nextConnections;
                ConnectionStreamSnapshot published = snapshot with { TransferRates = rates };

                await DispatchForEpochAsync(session.Epoch, () =>
                {
                    ConnectionSnapshot = published;
                    MarkLiveContact();
                }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await ReportFailureAsync(session, "connection stream", exception).ConfigureAwait(false);
        }
    }

    private async Task PumpTrafficAsync(
        IBackendSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            long lastPublishedAt = 0;
            await foreach (ClashTrafficSample sample in session.StreamClient
                .StreamTrafficAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                if (!ShouldPublish(ref lastPublishedAt, TimeSpan.FromMilliseconds(200)))
                {
                    continue;
                }

                await DispatchForEpochAsync(session.Epoch, () =>
                {
                    TrafficSample = sample;
                    MarkLiveContact();
                }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await ReportFailureAsync(session, "traffic stream", exception).ConfigureAwait(false);
        }
    }

    private async Task PumpMemoryAsync(
        IBackendSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            long lastPublishedAt = 0;
            await foreach (ClashMemorySample sample in session.StreamClient
                .StreamMemoryAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                if (!ShouldPublish(ref lastPublishedAt, TimeSpan.FromMilliseconds(500)))
                {
                    continue;
                }

                await DispatchForEpochAsync(session.Epoch, () =>
                {
                    MemorySample = sample;
                    MarkLiveContact();
                }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await ReportFailureAsync(session, "memory stream", exception).ConfigureAwait(false);
        }
    }

    private async Task PumpHonkRuntimeStatisticsAsync(
        IBackendSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            using PeriodicTimer timer = new(HonkStatisticsInterval, _timeProvider);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (session.Capabilities.GetObservation(ClashCapability.RuntimeStatistics).Support ==
                    CapabilitySupport.Unsupported)
                {
                    continue;
                }

                await TryRefreshHonkRuntimeStatisticsAsync(session, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task PumpLogsAsync(
        IBackendSession session,
        CancellationToken cancellationToken)
    {
        long droppedWhileBatching = 0;
        Channel<BufferedLogMessage> channel = Channel.CreateBounded<BufferedLogMessage>(
            new BoundedChannelOptions(2_048)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            },
            _ => AddInterlockedSaturating(ref droppedWhileBatching, 1));
        Task producer = ProduceLogsAsync(session, channel.Writer, cancellationToken);

        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(100), _timeProvider);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                List<BufferedLogMessage> batch = new(256);
                while (batch.Count < 256 && channel.Reader.TryRead(out BufferedLogMessage message))
                {
                    batch.Add(message);
                }

                long droppedBeforeDisplay = AddSaturating(
                    Interlocked.Exchange(ref droppedWhileBatching, 0),
                    Interlocked.Exchange(ref _pendingTransportDroppedLogCount, 0));
                if (batch.Count > 0 || droppedBeforeDisplay > 0)
                {
                    DateTimeOffset receivedAt = _timeProvider.GetUtcNow();
                    await DispatchForEpochAsync(
                        session.Epoch,
                        () => AppendBufferedLogs(batch, receivedAt, droppedBeforeDisplay))
                        .ConfigureAwait(false);
                }

                if (producer.IsCompleted && channel.Reader.Completion.IsCompleted)
                {
                    long finalDroppedBeforeDisplay = AddSaturating(
                        Interlocked.Exchange(ref droppedWhileBatching, 0),
                        Interlocked.Exchange(ref _pendingTransportDroppedLogCount, 0));
                    if (finalDroppedBeforeDisplay > 0)
                    {
                        DateTimeOffset receivedAt = _timeProvider.GetUtcNow();
                        await DispatchForEpochAsync(
                            session.Epoch,
                            () => AppendBufferedLogs([], receivedAt, finalDroppedBeforeDisplay))
                            .ConfigureAwait(false);
                    }

                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            try
            {
                await producer.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private async Task ProduceLogsAsync(
        IBackendSession session,
        ChannelWriter<BufferedLogMessage> writer,
        CancellationToken cancellationToken)
    {
        Exception? terminalError = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                long generation = Volatile.Read(ref _logGeneration);
                using CancellationTokenSource iterationSource =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                lock (_logIterationGate)
                {
                    if (generation != Volatile.Read(ref _logGeneration))
                    {
                        continue;
                    }

                    _logIterationSource = iterationSource;
                }

                bool restartRequested = false;
                try
                {
                    await foreach (ClashLogMessage message in session.StreamClient
                        .StreamLogsAsync(ClashLogLevel.Debug, iterationSource.Token)
                        .ConfigureAwait(false))
                    {
                        await writer.WriteAsync(
                            new BufferedLogMessage(generation, message),
                            iterationSource.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (iterationSource.IsCancellationRequested)
                {
                    restartRequested = !cancellationToken.IsCancellationRequested;
                }
                finally
                {
                    lock (_logIterationGate)
                    {
                        if (ReferenceEquals(_logIterationSource, iterationSource))
                        {
                            _logIterationSource = null;
                        }

                        restartRequested |= generation != Volatile.Read(ref _logGeneration);
                    }
                }

                if (!restartRequested)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            terminalError = exception;
            await ReportFailureAsync(session, "log stream", exception).ConfigureAwait(false);
        }
        finally
        {
            writer.TryComplete(terminalError);
        }
    }

    private void AppendLogs(
        IReadOnlyList<ClashLogMessage> messages,
        DateTimeOffset receivedAt,
        long droppedBeforeDisplay = 0)
    {
        int capacity = _settings.LogBufferSize;
        SessionLogEntry[] added = messages.Select(message => new SessionLogEntry(
                ++_nextLogSequence,
                receivedAt,
                message.Level,
                message.RawLevel,
                message.Payload))
            .ToArray();
        SessionLogEntry[] retainedAdded = added.Length <= capacity
            ? added
            : added[^capacity..];
        int removedCount = Math.Max(
            0,
            _logs.Count + retainedAdded.Length - capacity);
        _logs.ApplyDelta(removedCount, retainedAdded);
        DroppedLogCount = AddSaturating(DroppedLogCount, droppedBeforeDisplay);
        LogsChanged?.Invoke(
            this,
            new SessionLogsChangedEventArgs(
                retainedAdded,
                removedCount,
                droppedBeforeDisplay: droppedBeforeDisplay));
    }

    private void AppendBufferedLogs(
        IReadOnlyList<BufferedLogMessage> buffered,
        DateTimeOffset receivedAt,
        long droppedBeforeDisplay)
    {
        long generation = Volatile.Read(ref _logGeneration);
        ClashLogMessage[] current = buffered
            .Where(entry => entry.Generation == generation)
            .Select(static entry => entry.Message)
            .ToArray();
        AppendLogs(current, receivedAt, droppedBeforeDisplay);
    }

    private void TrimLogs(int capacity)
    {
        int removedCount = TrimLogsCore(capacity);
        if (removedCount > 0)
        {
            LogsChanged?.Invoke(
                this,
                new SessionLogsChangedEventArgs([], removedCount));
        }
    }

    private int TrimLogsCore(int capacity)
    {
        int removeCount = Math.Max(0, _logs.Count - capacity);
        if (removeCount > 0)
        {
            _logs.RemoveFirst(removeCount);
        }

        return removeCount;
    }

    private async Task ReportFailureAsync(
        IBackendSession session,
        string operation,
        Exception exception,
        bool isResourceFailure = false)
    {
        await DispatchForEpochAsync(session.Epoch, () =>
        {
            BackendConnectionState state = exception is ClashAuthenticationException
                ? BackendConnectionState.Unauthorized
                : BackendConnectionState.Degraded;
            if (SessionSnapshot.State == BackendConnectionState.Unauthorized &&
                state != BackendConnectionState.Unauthorized)
            {
                return;
            }

            string detail = $"The {operation} failed: {exception.Message}";
            if (isResourceFailure)
            {
                _resourceFailures[operation] = detail;
            }

            LastErrorMessage = detail;
            SessionSnapshot = SessionSnapshot with
            {
                State = state,
                StateChangedAt = _timeProvider.GetUtcNow(),
                StatusDetail = detail,
                Capabilities = session.Capabilities.GetSnapshot(),
            };
        }).ConfigureAwait(false);
    }

    private void MarkLiveContact()
    {
        if (SessionSnapshot.State == BackendConnectionState.Unauthorized)
        {
            return;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        BackendConnectionState state = GetStreamConnectionState() ?? BackendConnectionState.Online;
        string? resourceFailure = GetResourceFailureDetail();
        if (resourceFailure is not null && state != BackendConnectionState.Unauthorized)
        {
            state = BackendConnectionState.Degraded;
        }

        string? detail = resourceFailure ?? (state == BackendConnectionState.Online
            ? null
            : SessionSnapshot.StatusDetail);
        bool stateChanged = SessionSnapshot.State != state;
        bool detailChanged = !string.Equals(
            SessionSnapshot.StatusDetail,
            detail,
            StringComparison.Ordinal);
        bool contactTimestampDue = !SessionSnapshot.LastSuccessfulContactAt.HasValue ||
            now - SessionSnapshot.LastSuccessfulContactAt.Value >= LiveContactPublishInterval;
        if (!stateChanged && !detailChanged && !contactTimestampDue)
        {
            return;
        }

        SessionSnapshot = SessionSnapshot with
        {
            State = state,
            StateChangedAt = stateChanged ? now : SessionSnapshot.StateChangedAt,
            LastSuccessfulContactAt = contactTimestampDue || stateChanged
                ? now
                : SessionSnapshot.LastSuccessfulContactAt,
            StatusDetail = detail,
        };
        LastErrorMessage = detail;
    }

    private void OnStreamStatusChanged(
        object? sender,
        ClashStreamStatusChangedEventArgs args)
    {
        IBackendSession? session = ActiveSession;
        if (session is null || !ReferenceEquals(sender, session.StreamClient))
        {
            return;
        }

        ObserveDispatch(DispatchForEpochAsync(
            session.Epoch,
            () => ApplyStreamStatus(session, args.Status)));
    }

    private void OnStreamItemsDropped(
        object? sender,
        ClashStreamItemsDroppedEventArgs args)
    {
        IBackendSession? session = ActiveSession;
        if (session is null ||
            !ReferenceEquals(sender, session.StreamClient) ||
            args.Kind != ClashStreamKind.Logs)
        {
            return;
        }

        AddInterlockedSaturating(ref _pendingTransportDroppedLogCount, args.Count);
    }

    private void OnCapabilityChanged(
        object? sender,
        CapabilityChangedEventArgs args)
    {
        IBackendSession? session = ActiveSession;
        if (session is null || !ReferenceEquals(sender, session.Capabilities))
        {
            return;
        }

        ObserveDispatch(DispatchForEpochAsync(session.Epoch, () =>
        {
            CapabilityObservation observation = session.Capabilities.GetObservation(args.Capability);
            if (observation.Support == CapabilitySupport.Unsupported)
            {
                if (args.Capability == ClashCapability.SmartWeights)
                {
                    SmartWeights = null;
                }
                else if (args.Capability == ClashCapability.RuntimeStatistics)
                {
                    HonkRuntimeStatistics = null;
                }
            }

            SessionSnapshot = SessionSnapshot with
            {
                Capabilities = session.Capabilities.GetSnapshot(),
            };
        }));
    }

    private void ApplyStreamStatus(IBackendSession session, ClashStreamStatus status)
    {
        _streamStatuses[status.Kind] = status;
        if (status.State != ClashStreamState.Connected)
        {
            if (status.Kind == ClashStreamKind.Traffic)
            {
                TrafficSample = null;
            }
            else if (status.Kind == ClashStreamKind.Memory)
            {
                MemorySample = null;
            }
        }

        BackendConnectionState? nextState = GetStreamConnectionState();
        if (!nextState.HasValue || SessionSnapshot.State == BackendConnectionState.Unauthorized)
        {
            return;
        }

        string? resourceFailure = GetResourceFailureDetail();
        if (resourceFailure is not null && nextState.Value != BackendConnectionState.Unauthorized)
        {
            nextState = BackendConnectionState.Degraded;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        string? detail = resourceFailure ?? (nextState.Value switch
        {
            BackendConnectionState.Unauthorized =>
                $"The {status.Kind.ToString().ToLowerInvariant()} stream was rejected by the backend.",
            BackendConnectionState.Degraded =>
                $"The {status.Kind.ToString().ToLowerInvariant()} stream is unavailable: {status.Detail}",
            BackendConnectionState.OfflineRetrying =>
                $"The {status.Kind.ToString().ToLowerInvariant()} stream disconnected and is retrying.",
            _ => null,
        });

        SessionSnapshot = SessionSnapshot with
        {
            State = nextState.Value,
            StateChangedAt = SessionSnapshot.State == nextState.Value
                ? SessionSnapshot.StateChangedAt
                : now,
            LastSuccessfulContactAt = nextState.Value == BackendConnectionState.Online
                ? now
                : SessionSnapshot.LastSuccessfulContactAt,
            StatusDetail = detail,
            Capabilities = session.Capabilities.GetSnapshot(),
        };
        LastErrorMessage = detail;
    }

    private BackendConnectionState? GetStreamConnectionState()
    {
        if (_streamStatuses.Values.Any(static status =>
            status.State == ClashStreamState.Unauthorized))
        {
            return BackendConnectionState.Unauthorized;
        }

        if (_streamStatuses.Values.Any(static status =>
            status.State is ClashStreamState.Unsupported or ClashStreamState.Faulted))
        {
            return BackendConnectionState.Degraded;
        }

        if (_streamStatuses.Values.Any(static status =>
            status.State == ClashStreamState.Retrying ||
            (status.State == ClashStreamState.Connecting && status.RetryAttempt > 0)))
        {
            return _streamStatuses.Values.Any(static status =>
                status.State == ClashStreamState.Connected)
                ? BackendConnectionState.Degraded
                : BackendConnectionState.OfflineRetrying;
        }

        return _streamStatuses.Count > 0 && _streamStatuses.Values.All(static status =>
            status.State == ClashStreamState.Connected)
            ? BackendConnectionState.Online
            : null;
    }

    private string? GetResourceFailureDetail() =>
        _resourceFailures.Count == 0 ? null : _resourceFailures.Values.First();

    private bool ShouldPublish(ref long lastPublishedAt, TimeSpan interval)
    {
        long now = _timeProvider.GetTimestamp();
        if (lastPublishedAt != 0 && _timeProvider.GetElapsedTime(lastPublishedAt, now) < interval)
        {
            return false;
        }

        lastPublishedAt = now;
        return true;
    }

    private ValueTask DispatchForEpochAsync(SessionEpoch epoch, Action action)
    {
        if (Epoch != epoch)
        {
            return ValueTask.CompletedTask;
        }

        return _dispatcher.InvokeAsync(() =>
        {
            if (Epoch == epoch)
            {
                action();
            }
        });
    }

    private void ResetSessionState(SessionEpoch epoch, BackendProfile? profile)
    {
        ActiveProfile = profile;
        Configuration = null;
        ProxyCatalog = null;
        ProxyProviders = null;
        SmartWeights = null;
        RuleCatalog = null;
        RuleProviders = null;
        ConnectionSnapshot = null;
        TrafficSample = null;
        MemorySample = null;
        HonkRuntimeStatistics = null;
        LastErrorMessage = null;
        IsRefreshing = false;
        int removedLogCount = _logs.Count;
        _logs.Clear();
        DroppedLogCount = 0;
        Interlocked.Exchange(ref _pendingTransportDroppedLogCount, 0);
        LogsChanged?.Invoke(
            this,
            new SessionLogsChangedEventArgs([], removedLogCount, isReset: true));
        _streamStatuses.Clear();
        _resourceFailures.Clear();

        SessionSnapshot = profile is null
            ? BackendSessionSnapshot.NoBackend(epoch, _timeProvider.GetUtcNow())
            : new BackendSessionSnapshot
            {
                Epoch = epoch,
                ProfileId = profile.Id,
                State = BackendConnectionState.Connecting,
                StateChangedAt = _timeProvider.GetUtcNow(),
            };
        OnPropertyChanged(nameof(HasActiveSession));
    }

    private IBackendSession GetRequiredSession()
    {
        ThrowIfDisposed();
        return ActiveSession
            ?? throw new InvalidOperationException("No backend session is active.");
    }

    private static CancellationTokenSource CreateOperationSource(
        IBackendSession session,
        CancellationToken cancellationToken) =>
        CancellationTokenSource.CreateLinkedTokenSource(session.Lifetime, cancellationToken);

    private static void ValidateCredentialUpdate(BackendSaveRequest request)
    {
        if (request.CredentialUpdate is not BackendCredentialUpdate.Keep and
            not BackendCredentialUpdate.Replace and
            not BackendCredentialUpdate.Remove)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "The credential update mode is invalid.");
        }

        bool hasSecret = !string.IsNullOrEmpty(request.Secret);
        if (request.CredentialUpdate == BackendCredentialUpdate.Replace && !hasSecret)
        {
            throw new ArgumentException(
                "A replacement secret is required when replacing the saved credential.",
                nameof(request));
        }

        if (request.CredentialUpdate != BackendCredentialUpdate.Replace && hasSecret)
        {
            throw new ArgumentException(
                "A secret can only be supplied when replacing the saved credential.",
                nameof(request));
        }
    }

    private BackendProfile? FindProfile(Guid profileId)
    {
        BackendProfileSet profileSet = GetProfileSet();
        return profileSet.Profiles.FirstOrDefault(profile => profile.Id == profileId);
    }

    private BackendProfileSet GetProfileSet()
    {
        lock (_profileStateGate)
        {
            return _profileSet;
        }
    }

    private void SetProfileSet(BackendProfileSet value)
    {
        lock (_profileStateGate)
        {
            _profileSet = value;
        }
    }

    private void SetProfileState(
        BackendProfileSet value,
        IEnumerable<Guid> profilesWithCredentials)
    {
        lock (_profileStateGate)
        {
            _profileSet = value;
            _profilesWithStoredCredentials.Clear();
            _profilesWithStoredCredentials.UnionWith(profilesWithCredentials);
        }
    }

    private void SetCredentialPresence(Guid profileId, bool hasCredential)
    {
        lock (_profileStateGate)
        {
            if (hasCredential)
            {
                _profilesWithStoredCredentials.Add(profileId);
            }
            else
            {
                _profilesWithStoredCredentials.Remove(profileId);
            }
        }
    }

    private static void ReplaceCollection<T>(
        ObservableCollection<T> destination,
        IEnumerable<T> source)
    {
        CollectionBatch.Replace(destination, source);
    }

    private static long AddSaturating(long left, long right) => right <= 0
        ? left
        : left >= long.MaxValue - right
            ? long.MaxValue
            : left + right;

    private readonly record struct BufferedLogMessage(
        long Generation,
        ClashLogMessage Message);

    private static void AddInterlockedSaturating(ref long location, long value)
    {
        long current = Volatile.Read(ref location);
        while (current < long.MaxValue)
        {
            long next = AddSaturating(current, value);
            long observed = Interlocked.CompareExchange(ref location, next, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(AppSettingsState.LogBufferSize))
        {
            return;
        }

        if (_dispatcher.HasThreadAccess)
        {
            TrimLogs(_settings.LogBufferSize);
            return;
        }

        ObserveDispatch(_dispatcher.InvokeAsync(() => TrimLogs(_settings.LogBufferSize)));
    }

    private static async void ObserveDispatch(ValueTask dispatch)
    {
        try
        {
            await dispatch.ConfigureAwait(false);
        }
        catch
        {
            // Application shutdown can invalidate a queued UI dispatch.
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposeState != 0, this);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0)
        {
            await _disposeCompletion.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            _settings.PropertyChanged -= OnSettingsPropertyChanged;
            await _shutdownSource.CancelAsync().ConfigureAwait(false);
            await _userOperationGate.WaitAsync().ConfigureAwait(false);
            await _initializationGate.WaitAsync().ConfigureAwait(false);
            await _profileWriteGate.WaitAsync().ConfigureAwait(false);
            await _refreshGate.WaitAsync().ConfigureAwait(false);
            await _sessionGate.WaitAsync().ConfigureAwait(false);
            try
            {
                _ = Interlocked.Increment(ref _epochValue);
                await StopCurrentSessionAsync().ConfigureAwait(false);
            }
            finally
            {
                _sessionGate.Release();
                _refreshGate.Release();
                _profileWriteGate.Release();
                _initializationGate.Release();
                _userOperationGate.Release();
            }

            _disposeCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            _disposeCompletion.TrySetException(exception);
            throw;
        }

        // Keep the lifetime gates available until collection. A command can pass its
        // initial disposed check immediately before shutdown begins; the cancelled
        // shutdown token then turns that race into cancellation instead of accessing
        // an already-disposed synchronization primitive.
    }
}
