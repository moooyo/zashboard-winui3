using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Zashboard.App.Services;
using Zashboard.Core.Abstractions;
using Zashboard.Core.Backends;
using Zashboard.Core.Capabilities;
using Zashboard.Core.Clash;
using Zashboard.Core.Sessions;
using Zashboard.Infrastructure.Persistence;

namespace Zashboard.App.Logic.Tests;

internal sealed class FakeBackendProfileStore : IBackendProfileStore
{
    public BackendProfileSet Current { get; set; } = new();

    public Exception? SaveException { get; set; }

    public List<BackendProfileSet> Saves { get; } = [];

    public ValueTask<BackendProfileSet> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Current);
    }

    public ValueTask SaveAsync(
        BackendProfileSet profiles,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SaveException is not null)
        {
            throw SaveException;
        }

        Current = profiles;
        Saves.Add(profiles);
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeBackendCredentialStore : IBackendCredentialStore
{
    private readonly Dictionary<Guid, BackendCredential> _credentials = [];

    public Exception? DeleteException { get; set; }

    public List<Guid> GetCalls { get; } = [];

    public List<(Guid ProfileId, BackendCredential Credential)> SetCalls { get; } = [];

    public List<Guid> DeleteCalls { get; } = [];

    public ValueTask<bool> ExistsAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_credentials.ContainsKey(profileId));
    }

    public ValueTask<BackendCredential?> GetAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GetCalls.Add(profileId);
        _credentials.TryGetValue(profileId, out BackendCredential? credential);
        return ValueTask.FromResult(credential);
    }

    public ValueTask SetAsync(
        Guid profileId,
        BackendCredential credential,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetCalls.Add((profileId, credential));
        _credentials[profileId] = credential;
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeleteCalls.Add(profileId);
        if (DeleteException is not null)
        {
            throw DeleteException;
        }

        _credentials.Remove(profileId);
        return ValueTask.CompletedTask;
    }

    public BackendCredential? GetStored(Guid profileId) =>
        _credentials.GetValueOrDefault(profileId);

    public void Seed(Guid profileId, string secret) =>
        _credentials[profileId] = new BackendCredential(secret);
}

internal sealed record FakeSessionFactoryCall(
    BackendProfile Profile,
    BackendCredential Credential,
    SessionEpoch Epoch);

internal sealed class FakeBackendSessionFactory : IBackendSessionFactory
{
    public Func<BackendProfile, BackendCredential, SessionEpoch, CancellationToken,
        ValueTask<IBackendSession>>? CreateHandler { get; init; }

    public List<FakeSessionFactoryCall> Calls { get; } = [];

    public List<IBackendSession> CreatedSessions { get; } = [];

    public async ValueTask<IBackendSession> CreateAsync(
        BackendProfile profile,
        BackendCredential credential,
        SessionEpoch epoch,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(new FakeSessionFactoryCall(profile, credential, epoch));
        IBackendSession session = CreateHandler is null
            ? new FakeBackendSession(epoch, profile)
            : await CreateHandler(profile, credential, epoch, cancellationToken)
                .ConfigureAwait(false);
        CreatedSessions.Add(session);
        return session;
    }
}

internal sealed class FakeBackendSession : IBackendSession
{
    private readonly CancellationTokenSource _lifetimeSource = new();
    private readonly Exception? _disposeException;
    private readonly BackendSessionSnapshot _snapshot;
    private int _disposeState;
    private int _disposeCount;

    public FakeBackendSession(
        SessionEpoch epoch,
        BackendProfile profile,
        bool holdStreamsAfterCancellation = false,
        Exception? disposeException = null)
    {
        Epoch = epoch;
        Profile = profile;
        _disposeException = disposeException;
        Capabilities = new CapabilityRegistry();
        Rest = new FakeClashRestClient();
        Streams = new FakeClashStreamClient(holdStreamsAfterCancellation);
        _snapshot = new BackendSessionSnapshot
        {
            Epoch = epoch,
            ProfileId = profile.Id,
            State = BackendConnectionState.Online,
            CoreKind = ClashCoreKind.Mihomo,
            Version = "Mihomo Meta test",
            StateChangedAt = DateTimeOffset.UnixEpoch,
            LastSuccessfulContactAt = DateTimeOffset.UnixEpoch,
            Capabilities = Capabilities.GetSnapshot(),
        };
    }

    public SessionEpoch Epoch { get; }

    public BackendProfile Profile { get; }

    public FakeClashRestClient Rest { get; }

    public FakeClashStreamClient Streams { get; }

    public IClashRestClient RestClient => Rest;

    public IClashStreamClient StreamClient => Streams;

    public ICapabilityRegistry Capabilities { get; }

    public CancellationToken Lifetime => _lifetimeSource.Token;

    public BackendSessionSnapshot Snapshot => _snapshot with
    {
        Capabilities = Capabilities.GetSnapshot(),
    };

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public TaskCompletionSource DisposeStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _ = Interlocked.Increment(ref _disposeCount);
        DisposeStarted.TrySetResult();
        try
        {
            await _lifetimeSource.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifetimeSource.Dispose();
        }

        if (_disposeException is not null)
        {
            throw _disposeException;
        }
    }
}

internal sealed class FakeClashRestClient : IClashRestClient
{
    public Func<CancellationToken, Task>? FlushDnsCacheHandler { get; set; }

    public ProxyCatalog ProxiesResult { get; set; } = new();

    public SmartWeights SmartWeightsResult { get; set; } = new();

    public HonkRuntimeStatistics HonkStatisticsResult { get; set; } = new();

    public List<string> CloseConnectionCalls { get; } = [];

    public int CloseAllConnectionsCallCount { get; private set; }

    public List<string> BlockSmartConnectionCalls { get; } = [];

    public Task<ClashVersion> GetVersionAsync(CancellationToken cancellationToken = default) =>
        Result(new ClashVersion
        {
            Value = "Mihomo Meta test",
            CoreKind = ClashCoreKind.Mihomo,
        }, cancellationToken);

    public Task<ProxyCatalog> GetProxiesAsync(CancellationToken cancellationToken = default) =>
        Result(ProxiesResult, cancellationToken);

    public Task SelectProxyAsync(
        string groupName,
        string proxyName,
        CancellationToken cancellationToken = default) =>
        Complete(cancellationToken);

    public Task ClearFixedProxyAsync(
        string groupName,
        CancellationToken cancellationToken = default) =>
        Complete(cancellationToken);

    public Task<ProxyDelay> MeasureProxyDelayAsync(
        string proxyName,
        ProxyDelayRequest request,
        CancellationToken cancellationToken = default) =>
        Result(new ProxyDelay(1), cancellationToken);

    public Task<ProxyDelay> MeasureProviderProxyDelayAsync(
        string providerName,
        string proxyName,
        ProxyDelayRequest request,
        CancellationToken cancellationToken = default) =>
        Result(new ProxyDelay(1), cancellationToken);

    public Task<IReadOnlyDictionary<string, ProxyDelay>> MeasureProxyGroupDelayAsync(
        string groupName,
        ProxyDelayRequest request,
        CancellationToken cancellationToken = default) =>
        Result<IReadOnlyDictionary<string, ProxyDelay>>(
            new Dictionary<string, ProxyDelay>(StringComparer.Ordinal),
            cancellationToken);

    public Task<ProxyProviderCatalog> GetProxyProvidersAsync(
        CancellationToken cancellationToken = default) =>
        Result(new ProxyProviderCatalog(), cancellationToken);

    public Task UpdateProxyProviderAsync(
        string providerName,
        CancellationToken cancellationToken = default) =>
        Complete(cancellationToken);

    public Task CheckProxyProviderAsync(
        string providerName,
        CancellationToken cancellationToken = default) =>
        Complete(cancellationToken);

    public Task<RuleCatalog> GetRulesAsync(CancellationToken cancellationToken = default) =>
        Result(new RuleCatalog(), cancellationToken);

    public Task<RuleProviderCatalog> GetRuleProvidersAsync(
        CancellationToken cancellationToken = default) =>
        Result(new RuleProviderCatalog(), cancellationToken);

    public Task UpdateRuleProviderAsync(
        string providerName,
        CancellationToken cancellationToken = default) =>
        Complete(cancellationToken);

    public Task SetRuleDisabledStatesAsync(
        IReadOnlyDictionary<int, bool> disabledStates,
        CancellationToken cancellationToken = default) =>
        Complete(cancellationToken);

    public Task ToggleRuleDisabledByIdentifierAsync(
        string identifier,
        CancellationToken cancellationToken = default) =>
        Complete(cancellationToken);

    public Task CloseConnectionAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CloseConnectionCalls.Add(connectionId);
        return Task.CompletedTask;
    }

    public Task CloseAllConnectionsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CloseAllConnectionsCallCount++;
        return Task.CompletedTask;
    }

    public Task BlockSmartConnectionAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BlockSmartConnectionCalls.Add(connectionId);
        return Task.CompletedTask;
    }

    public Task<ClashConfiguration> GetConfigurationAsync(
        CancellationToken cancellationToken = default) =>
        Result(new ClashConfiguration { Mode = "rule" }, cancellationToken);

    public Task PatchConfigurationAsync(
        ClashConfigurationPatch patch,
        CancellationToken cancellationToken = default) =>
        Complete(cancellationToken);

    public Task ReloadConfigurationAsync(CancellationToken cancellationToken = default) =>
        Complete(cancellationToken);

    public Task UpdateConfigurationAsync(
        ClashConfigurationUpdate update,
        CancellationToken cancellationToken = default) =>
        Complete(cancellationToken);

    public Task FlushFakeIpCacheAsync(CancellationToken cancellationToken = default) =>
        Complete(cancellationToken);

    public Task FlushDnsCacheAsync(CancellationToken cancellationToken = default) =>
        FlushDnsCacheHandler?.Invoke(cancellationToken) ?? Complete(cancellationToken);

    public Task<DnsQueryResult> QueryDnsAsync(
        DnsQueryRequest request,
        CancellationToken cancellationToken = default) =>
        Result(new DnsQueryResult(), cancellationToken);

    public Task UpdateGeoDataAsync(CancellationToken cancellationToken = default) =>
        Complete(cancellationToken);

    public Task UpgradeCoreAsync(
        CoreUpgradeChannel channel,
        CancellationToken cancellationToken = default) =>
        Complete(cancellationToken);

    public Task RestartCoreAsync(CancellationToken cancellationToken = default) =>
        Complete(cancellationToken);

    public Task UpgradeDashboardAsync(CancellationToken cancellationToken = default) =>
        Complete(cancellationToken);

    public Task<DashboardStorage> GetDashboardStorageAsync(
        CancellationToken cancellationToken = default) =>
        Result(new DashboardStorage(), cancellationToken);

    public Task SetDashboardStorageAsync(
        DashboardStorage storage,
        CancellationToken cancellationToken = default) =>
        Complete(cancellationToken);

    public Task DeleteDashboardStorageAsync(CancellationToken cancellationToken = default) =>
        Complete(cancellationToken);

    public Task<SmartWeights> GetSmartWeightsAsync(CancellationToken cancellationToken = default) =>
        Result(SmartWeightsResult, cancellationToken);

    public Task FlushSmartWeightsAsync(CancellationToken cancellationToken = default) =>
        Complete(cancellationToken);

    public Task<HonkRuntimeStatistics> GetHonkRuntimeStatisticsAsync(
        CancellationToken cancellationToken = default) =>
        Result(HonkStatisticsResult, cancellationToken);

    private static Task Complete(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private static Task<T> Result<T>(T value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(value);
    }
}

internal sealed class FakeClashStreamClient(bool holdAfterCancellation) : IClashStreamClient
{
    private EventHandler<ClashStreamStatusChangedEventArgs>? _streamStatusChanged;
    private EventHandler<ClashStreamItemsDroppedEventArgs>? _streamItemsDropped;

    public ControlledAsyncStream<ConnectionStreamSnapshot> Connections { get; } =
        new(holdAfterCancellation);

    public ControlledAsyncStream<ClashLogMessage> Logs { get; } =
        new(holdAfterCancellation);

    public ControlledAsyncStream<ClashTrafficSample> Traffic { get; } =
        new(holdAfterCancellation);

    public ControlledAsyncStream<ClashMemorySample> Memory { get; } =
        new(holdAfterCancellation);

    public event EventHandler<ClashStreamStatusChangedEventArgs>? StreamStatusChanged
    {
        add => _streamStatusChanged += value;
        remove => _streamStatusChanged -= value;
    }

    public event EventHandler<ClashStreamItemsDroppedEventArgs>? StreamItemsDropped
    {
        add => _streamItemsDropped += value;
        remove => _streamItemsDropped -= value;
    }

    public Task AllStarted => Task.WhenAll(
        Connections.Started.Task,
        Logs.Started.Task,
        Traffic.Started.Task,
        Memory.Started.Task);

    public Task AllCancellationObserved => Task.WhenAll(
        Connections.CancellationObserved.Task,
        Logs.CancellationObserved.Task,
        Traffic.CancellationObserved.Task,
        Memory.CancellationObserved.Task);

    public Task AllCompleted => Task.WhenAll(
        Connections.Completed.Task,
        Logs.Completed.Task,
        Traffic.Completed.Task,
        Memory.Completed.Task);

    public IAsyncEnumerable<ConnectionStreamSnapshot> StreamConnectionsAsync(
        CancellationToken cancellationToken = default) =>
        Connections.ReadAsync(cancellationToken);

    public IAsyncEnumerable<ClashLogMessage> StreamLogsAsync(
        ClashLogLevel minimumLevel,
        CancellationToken cancellationToken = default) =>
        Logs.ReadAsync(cancellationToken);

    public IAsyncEnumerable<ClashTrafficSample> StreamTrafficAsync(
        CancellationToken cancellationToken = default) =>
        Traffic.ReadAsync(cancellationToken);

    public IAsyncEnumerable<ClashMemorySample> StreamMemoryAsync(
        CancellationToken cancellationToken = default) =>
        Memory.ReadAsync(cancellationToken);

    public void ReleaseAll()
    {
        Connections.Release();
        Logs.Release();
        Traffic.Release();
        Memory.Release();
    }

    public void RaiseStatus(ClashStreamStatus status) =>
        _streamStatusChanged?.Invoke(this, new ClashStreamStatusChangedEventArgs(status));

    public void RaiseDropped(ClashStreamKind kind, long count) =>
        _streamItemsDropped?.Invoke(this, new ClashStreamItemsDroppedEventArgs(kind, count));

    public void PublishLog(ClashLogMessage message) => Logs.Publish(message);
}

internal sealed class ControlledAsyncStream<T>(bool holdAfterCancellation)
{
    private readonly Channel<T> _items = Channel.CreateUnbounded<T>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
    });
    private readonly object _deliveryGate = new();
    private readonly TaskCompletionSource _release = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _deliveryRelease;
    private TaskCompletionSource _deliveryBlocked = NewCompletion();
    private long _deliveredCount;
    private int _startedCount;

    public TaskCompletionSource Started { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource CancellationObserved { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Completed { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public long DeliveredCount => Volatile.Read(ref _deliveredCount);

    public int StartedCount => Volatile.Read(ref _startedCount);

    public Task DeliveryBlocked
    {
        get
        {
            lock (_deliveryGate)
            {
                return _deliveryBlocked.Task;
            }
        }
    }

    public async IAsyncEnumerable<T> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _ = Interlocked.Increment(ref _startedCount);
        Started.TrySetResult();
        try
        {
            await foreach (T item in _items.Reader
                .ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                Task? deliveryRelease;
                lock (_deliveryGate)
                {
                    deliveryRelease = _deliveryRelease?.Task;
                    if (deliveryRelease is not null)
                    {
                        _deliveryBlocked.TrySetResult();
                    }
                }

                if (deliveryRelease is not null)
                {
                    await deliveryRelease.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                yield return item;
                _ = Interlocked.Increment(ref _deliveredCount);
            }
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                if (holdAfterCancellation)
                {
                    await _release.Task.ConfigureAwait(false);
                }
            }

            Completed.TrySetResult();
        }
    }

    public void Release() => _release.TrySetResult();

    public void Publish(T item)
    {
        if (!_items.Writer.TryWrite(item))
        {
            throw new InvalidOperationException("The controlled stream is closed.");
        }
    }

    public void Complete() => _items.Writer.TryComplete();

    public void PauseDelivery()
    {
        lock (_deliveryGate)
        {
            if (_deliveryRelease is not null)
            {
                throw new InvalidOperationException("Delivery is already paused.");
            }

            _deliveryRelease = NewCompletion();
            _deliveryBlocked = NewCompletion();
        }
    }

    public void ResumeDelivery()
    {
        TaskCompletionSource release;
        lock (_deliveryGate)
        {
            release = _deliveryRelease
                ?? throw new InvalidOperationException("Delivery is not paused.");
            _deliveryRelease = null;
        }

        release.TrySetResult();
    }

    private static TaskCompletionSource NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class InlineTestDispatcher : IUiDispatcher
{
    public bool HasThreadAccess => true;

    public ValueTask InvokeAsync(
        Action action,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        action();
        return ValueTask.CompletedTask;
    }
}

internal sealed class QueuedTestDispatcher : IUiDispatcher
{
    private readonly object _gate = new();
    private TaskCompletionSource _queued = NewCompletion();
    private PendingDispatch? _pending;
    private bool _holdNext;

    public bool HasThreadAccess => true;

    public Task NextInvocationQueued
    {
        get
        {
            lock (_gate)
            {
                return _queued.Task;
            }
        }
    }

    public void HoldNextInvocation()
    {
        lock (_gate)
        {
            if (_holdNext || _pending is not null)
            {
                throw new InvalidOperationException("A dispatcher invocation is already held.");
            }

            _queued = NewCompletion();
            _holdNext = true;
        }
    }

    public ValueTask InvokeAsync(
        Action action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_holdNext)
            {
                _holdNext = false;
                PendingDispatch pending = new(action, cancellationToken);
                _pending = pending;
                _queued.TrySetResult();
                return new ValueTask(pending.Completion.Task);
            }
        }

        action();
        return ValueTask.CompletedTask;
    }

    public void ReleaseNext()
    {
        PendingDispatch pending;
        lock (_gate)
        {
            pending = _pending
                ?? throw new InvalidOperationException("No dispatcher invocation is held.");
            _pending = null;
        }

        pending.Run();
    }

    private static TaskCompletionSource NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class PendingDispatch(
        Action action,
        CancellationToken cancellationToken)
    {
        public TaskCompletionSource Completion { get; } = NewCompletion();

        public void Run()
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                action();
                Completion.TrySetResult();
            }
            catch (Exception exception)
            {
                Completion.TrySetException(exception);
            }
        }
    }
}
