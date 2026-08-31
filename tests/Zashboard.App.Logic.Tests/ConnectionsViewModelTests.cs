using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Zashboard.App.Controls;
using Zashboard.App.Services;
using Zashboard.App.ViewModels;
using Zashboard.Core.Abstractions;
using Zashboard.Core.Backends;
using Zashboard.Core.Capabilities;
using Zashboard.Core.Clash;
using Zashboard.Core.Sessions;
using Zashboard.Infrastructure.Persistence;

namespace Zashboard.App.Logic.Tests;

[TestClass]
public sealed class ConnectionsViewModelTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task StableRowsUpdateInPlaceWithoutCollectionReset()
    {
        (AppSessionCoordinator coordinator, ConnectionTestSession session, _) =
            await CreateActiveCoordinatorAsync();
        using ConnectionsViewModel viewModel = new(coordinator);
        session.Streams.Publish(Snapshot(Connection("stable", "first.example")));
        await WaitUntilAsync(() => viewModel.Connections.Count == 1);

        ConnectionDisplayItem row = viewModel.Connections[0];
        List<NotifyCollectionChangedAction> collectionActions = [];
        List<string?> propertyNames = [];
        viewModel.Connections.CollectionChanged += (_, args) =>
            collectionActions.Add(args.Action);
        row.PropertyChanged += (_, args) => propertyNames.Add(args.PropertyName);

        await Task.Delay(TimeSpan.FromMilliseconds(300));
        session.Streams.Publish(Snapshot(Connection("stable", "second.example")));
        await WaitUntilAsync(() => row.Host == "second.example");

        Assert.AreSame(row, viewModel.Connections[0]);
        Assert.IsEmpty(collectionActions);
        CollectionAssert.Contains(propertyNames, nameof(ConnectionDisplayItem.Host));
        await coordinator.DisposeAsync();
    }

    [TestMethod]
    public async Task PauseFreezesSnapshotCountAndSearchUntilLatestIsRequested()
    {
        (AppSessionCoordinator coordinator, ConnectionTestSession session, _) =
            await CreateActiveCoordinatorAsync();
        using ConnectionsViewModel viewModel = new(coordinator);

        session.Streams.Publish(Snapshot(Connection(
            "old",
            "old.example",
            process: "old-process")));
        await WaitUntilAsync(() => viewModel.TotalCount == 1);

        await viewModel.SetPausedAsync(true);
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        session.Streams.Publish(Snapshot(
            Connection("new-a", "new-a.example", process: "new-process"),
            Connection("new-b", "new-b.example")));
        await WaitUntilAsync(() => coordinator.ConnectionSnapshot?.Connections.Count == 2);

        await viewModel.SetQueryAsync("new-process");

        Assert.IsTrue(viewModel.IsPaused);
        Assert.AreEqual(1, viewModel.TotalCount);
        Assert.IsEmpty(viewModel.Connections);

        await viewModel.SetQueryAsync("old-process");

        Assert.HasCount(1, viewModel.Connections);
        Assert.AreEqual("old", viewModel.Connections[0].Id);

        await viewModel.RefreshAsync();

        Assert.IsFalse(viewModel.IsPaused);
        Assert.AreEqual(2, viewModel.TotalCount);
        Assert.IsEmpty(viewModel.Connections);

        await viewModel.SetQueryAsync(null);

        Assert.HasCount(2, viewModel.Connections);
        await coordinator.DisposeAsync();
    }

    [TestMethod]
    public async Task MutatingActionsIgnorePausedAndStaleConnections()
    {
        (AppSessionCoordinator coordinator, ConnectionTestSession session, _) =
            await CreateActiveCoordinatorAsync();
        using ConnectionsViewModel viewModel = new(coordinator);

        session.Streams.Publish(Snapshot(Connection("old", "old.example")));
        await WaitUntilAsync(() => viewModel.TotalCount == 1);
        await viewModel.SetPausedAsync(true);

        await viewModel.DisconnectAsync("old");
        await viewModel.BlockAsync("old");
        await viewModel.DisconnectAllAsync();

        Assert.IsEmpty(session.Rest.CloseConnectionCalls);
        Assert.IsEmpty(session.Rest.BlockSmartConnectionCalls);
        Assert.AreEqual(0, session.Rest.CloseAllConnectionsCallCount);
        Assert.AreEqual("old", coordinator.ConnectionSnapshot?.Connections.Single().Id);

        await viewModel.RefreshAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        session.Streams.Publish(Snapshot(Connection("current", "current.example")));
        await WaitUntilAsync(() => viewModel.Connections.SingleOrDefault()?.Id == "current");

        await viewModel.DisconnectAsync("old");
        await viewModel.BlockAsync("old");

        Assert.IsEmpty(session.Rest.CloseConnectionCalls);
        Assert.IsEmpty(session.Rest.BlockSmartConnectionCalls);
        Assert.AreEqual(0, session.Rest.CloseAllConnectionsCallCount);
        Assert.AreEqual("current", coordinator.ConnectionSnapshot?.Connections.Single().Id);

        await viewModel.DisconnectAsync("current");
        await WaitUntilAsync(() => viewModel.TotalCount == 0);

        Assert.HasCount(1, session.Rest.CloseConnectionCalls);
        Assert.AreEqual("current", session.Rest.CloseConnectionCalls[0]);
        Assert.IsEmpty(coordinator.ConnectionSnapshot?.Connections ?? []);
        Assert.IsEmpty(viewModel.Connections);
        await coordinator.DisposeAsync();
    }

    [TestMethod]
    public async Task EpochSwitchClearsPausedSnapshotAndRejectsMutatingActions()
    {
        (AppSessionCoordinator coordinator, ConnectionTestSession firstSession,
            FakeBackendSessionFactory sessions) = await CreateActiveCoordinatorAsync();
        using ConnectionsViewModel viewModel = new(coordinator);

        firstSession.Streams.Publish(Snapshot(Connection("old", "old.example")));
        await WaitUntilAsync(() => viewModel.TotalCount == 1);
        await viewModel.SetPausedAsync(true);

        await coordinator.RunUserOperationAsync(coordinator.ReconnectAsync);
        ConnectionTestSession secondSession = (ConnectionTestSession)sessions.CreatedSessions[^1];
        await secondSession.Streams.AllStarted.WaitAsync(TestTimeout);
        await WaitUntilAsync(() => viewModel.TotalCount == 0 && viewModel.Connections.Count == 0);

        Assert.AreNotEqual(firstSession.Epoch, secondSession.Epoch);
        Assert.IsTrue(viewModel.IsPaused);

        secondSession.Streams.Publish(Snapshot(Connection("new", "new.example")));
        await WaitUntilAsync(() => coordinator.ConnectionSnapshot?.Connections.Count == 1);

        await viewModel.DisconnectAsync("old");
        await viewModel.DisconnectAsync("new");
        await viewModel.BlockAsync("old");
        await viewModel.BlockAsync("new");
        await viewModel.DisconnectAllAsync();

        Assert.IsEmpty(firstSession.Rest.CloseConnectionCalls);
        Assert.IsEmpty(firstSession.Rest.BlockSmartConnectionCalls);
        Assert.AreEqual(0, firstSession.Rest.CloseAllConnectionsCallCount);
        Assert.IsEmpty(secondSession.Rest.CloseConnectionCalls);
        Assert.IsEmpty(secondSession.Rest.BlockSmartConnectionCalls);
        Assert.AreEqual(0, secondSession.Rest.CloseAllConnectionsCallCount);
        Assert.AreEqual(0, viewModel.TotalCount);
        Assert.IsEmpty(viewModel.Connections);

        await coordinator.DisposeAsync();
    }

    private static async Task<(AppSessionCoordinator Coordinator, ConnectionTestSession Session,
        FakeBackendSessionFactory Sessions)>
        CreateActiveCoordinatorAsync()
    {
        BackendProfile profile = new(
            Guid.NewGuid(),
            "Connections backend",
            BackendEndpoint.Create("http://127.0.0.1:9090"));
        FakeBackendProfileStore profiles = new()
        {
            Current = new BackendProfileSet
            {
                Profiles = [profile],
                ActiveProfileId = profile.Id,
            },
        };
        ConnectionTestSession? session = null;
        FakeBackendSessionFactory sessions = new()
        {
            CreateHandler = (createdProfile, _, epoch, _) =>
            {
                session = new ConnectionTestSession(epoch, createdProfile);
                return ValueTask.FromResult<IBackendSession>(session);
            },
        };
        AppSessionCoordinator coordinator = new(
            profiles,
            new FakeBackendCredentialStore(),
            sessions,
            new InlineTestDispatcher(),
            new AppSettingsState(),
            TimeProvider.System);

        await coordinator.RunUserOperationAsync(coordinator.InitializeAsync);
        ConnectionTestSession createdSession = session
            ?? throw new InvalidOperationException("The test session was not created.");
        await createdSession.Streams.AllStarted.WaitAsync(TestTimeout);
        return (coordinator, createdSession, sessions);
    }

    private static ClashConnection Connection(
        string id,
        string host,
        string process = "") => new()
    {
        Id = id,
        StartedAt = DateTimeOffset.UnixEpoch,
        Rule = "MATCH",
        Chains = ["DIRECT"],
        Metadata = new ClashConnectionMetadata
        {
            Host = host,
            Network = "tcp",
            Process = process,
        },
    };

    private static ConnectionStreamSnapshot Snapshot(params ClashConnection[] connections) => new()
    {
        Connections = connections,
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TestTimeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                Assert.Fail("The expected connection state was not observed before the timeout.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class ConnectionTestSession : IBackendSession
    {
        private readonly CancellationTokenSource _lifetimeSource = new();
        private readonly BackendSessionSnapshot _snapshot;

        public ConnectionTestSession(SessionEpoch epoch, BackendProfile profile)
        {
            Epoch = epoch;
            Profile = profile;
            Rest = new FakeClashRestClient();
            Streams = new ConnectionTestStreamClient();
            Capabilities = new CapabilityRegistry();
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

        public IClashRestClient RestClient => Rest;

        public ConnectionTestStreamClient Streams { get; }

        public IClashStreamClient StreamClient => Streams;

        public ICapabilityRegistry Capabilities { get; }

        public CancellationToken Lifetime => _lifetimeSource.Token;

        public BackendSessionSnapshot Snapshot => _snapshot with
        {
            Capabilities = Capabilities.GetSnapshot(),
        };

        public async ValueTask DisposeAsync()
        {
            await _lifetimeSource.CancelAsync();
            _lifetimeSource.Dispose();
        }
    }

    private sealed class ConnectionTestStreamClient : IClashStreamClient
    {
        private readonly Channel<ConnectionStreamSnapshot> _connections =
            Channel.CreateUnbounded<ConnectionStreamSnapshot>();
        private readonly TaskCompletionSource _allStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _startedCount;

        public event EventHandler<ClashStreamStatusChangedEventArgs>? StreamStatusChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<ClashStreamItemsDroppedEventArgs>? StreamItemsDropped
        {
            add { }
            remove { }
        }

        public Task AllStarted => _allStarted.Task;

        public void Publish(ConnectionStreamSnapshot snapshot)
        {
            if (!_connections.Writer.TryWrite(snapshot))
            {
                throw new InvalidOperationException("The connection test stream is closed.");
            }
        }

        public async IAsyncEnumerable<ConnectionStreamSnapshot> StreamConnectionsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            MarkStarted();
            await foreach (ConnectionStreamSnapshot snapshot in _connections.Reader
                .ReadAllAsync(cancellationToken))
            {
                yield return snapshot;
            }
        }

        public IAsyncEnumerable<ClashLogMessage> StreamLogsAsync(
            ClashLogLevel minimumLevel,
            CancellationToken cancellationToken = default) =>
            WaitForCancellationAsync<ClashLogMessage>(cancellationToken);

        public IAsyncEnumerable<ClashTrafficSample> StreamTrafficAsync(
            CancellationToken cancellationToken = default) =>
            WaitForCancellationAsync<ClashTrafficSample>(cancellationToken);

        public IAsyncEnumerable<ClashMemorySample> StreamMemoryAsync(
            CancellationToken cancellationToken = default) =>
            WaitForCancellationAsync<ClashMemorySample>(cancellationToken);

        private async IAsyncEnumerable<T> WaitForCancellationAsync<T>(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            MarkStarted();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        private void MarkStarted()
        {
            if (Interlocked.Increment(ref _startedCount) == 4)
            {
                _allStarted.TrySetResult();
            }
        }
    }
}
