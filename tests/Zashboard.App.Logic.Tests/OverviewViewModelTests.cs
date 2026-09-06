using System.Collections.Specialized;
using System.ComponentModel;
using Zashboard.App.Controls;
using Zashboard.App.Services;
using Zashboard.App.ViewModels;
using Zashboard.Core.Backends;
using Zashboard.Core.Clash;
using Zashboard.Core.Sessions;
using Zashboard.Infrastructure.Persistence;

namespace Zashboard.App.Logic.Tests;

[TestClass]
public sealed class OverviewViewModelTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task InactiveRecentRowsDeferProjectionWhileTelemetryHistoryContinues()
    {
        (AppSessionCoordinator coordinator, FakeBackendSession session) =
            await CreateCoordinatorAsync();
        using OverviewViewModel viewModel = new(coordinator);
        session.Streams.Connections.Publish(Snapshot(Connection("stable", "first.example")));
        await WaitUntilAsync(() => viewModel.RecentConnections.Count == 1);
        RecentConnectionDisplayItem row = viewModel.RecentConnections[0];
        viewModel.SetActive(false);
        List<NotifyCollectionChangedAction> actions = [];
        viewModel.RecentConnections.CollectionChanged += (_, args) => actions.Add(args.Action);

        session.Streams.Traffic.Publish(new ClashTrafficSample { Down = 4096, Up = 1024 });
        session.Streams.Memory.Publish(new ClashMemorySample { InUse = 64 * 1024 * 1024 });
        await Task.Delay(300);
        session.Streams.Connections.Publish(Snapshot(
            Connection("stable", "second.example"),
            Connection("new", "third.example")));
        await WaitUntilAsync(() =>
            viewModel.ConnectionCount == "2" &&
            viewModel.TrafficHistory.Count == 1 &&
            viewModel.MemoryHistory.Count == 1);

        Assert.AreEqual("first.example", row.Host);
        Assert.IsEmpty(actions);
        long trafficRevision = viewModel.TrafficRevision;
        long memoryRevision = viewModel.MemoryRevision;
        viewModel.SetActive(true);

        Assert.HasCount(2, viewModel.RecentConnections);
        Assert.AreSame(row, viewModel.RecentConnections[0]);
        Assert.AreEqual("second.example", row.Host);
        Assert.AreEqual(trafficRevision, viewModel.TrafficRevision);
        Assert.AreEqual(memoryRevision, viewModel.MemoryRevision);
        Assert.HasCount(1, viewModel.TrafficHistory);
        Assert.HasCount(1, viewModel.MemoryHistory);
        await coordinator.DisposeAsync();
    }

    [TestMethod]
    public async Task StableConnectionRowsUpdateInPlaceWithoutCollectionReset()
    {
        (AppSessionCoordinator coordinator, FakeBackendSession session) =
            await CreateCoordinatorAsync();
        using OverviewViewModel viewModel = new(coordinator);
        session.Streams.Connections.Publish(Snapshot(Connection("stable", "first.example")));
        await WaitUntilAsync(() => viewModel.RecentConnections.Count == 1);

        RecentConnectionDisplayItem row = viewModel.RecentConnections[0];
        List<NotifyCollectionChangedAction> collectionActions = [];
        List<string?> propertyNames = [];
        viewModel.RecentConnections.CollectionChanged += (_, args) =>
            collectionActions.Add(args.Action);
        row.PropertyChanged += (_, args) => propertyNames.Add(args.PropertyName);

        await Task.Delay(300);
        session.Streams.Connections.Publish(Snapshot(Connection("stable", "second.example")));
        await WaitUntilAsync(() => row.Host == "second.example");

        Assert.AreSame(row, viewModel.RecentConnections[0]);
        Assert.IsEmpty(collectionActions);
        CollectionAssert.Contains(propertyNames, nameof(RecentConnectionDisplayItem.Host));
        await coordinator.DisposeAsync();
    }

    [TestMethod]
    public async Task TrafficAndMemorySamplesPopulateChartHistory()
    {
        (AppSessionCoordinator coordinator, FakeBackendSession session) =
            await CreateCoordinatorAsync();
        using OverviewViewModel viewModel = new(coordinator);

        session.Streams.Traffic.Publish(new ClashTrafficSample
        {
            Down = 4096,
            Up = 1024,
            DownTotal = 8192,
            UpTotal = 2048,
        });
        session.Streams.Memory.Publish(new ClashMemorySample { InUse = 64 * 1024 * 1024 });
        await WaitUntilAsync(() =>
            viewModel.TrafficHistory.Count == 1 && viewModel.MemoryHistory.Count == 1);

        Assert.AreEqual(4096, viewModel.TrafficHistory[0].Download);
        Assert.AreEqual(1024, viewModel.TrafficHistory[0].Upload);
        Assert.AreEqual(64 * 1024 * 1024, viewModel.MemoryHistory[0].InUse);
        Assert.IsGreaterThan(0, viewModel.TrafficRevision);
        Assert.IsGreaterThan(0, viewModel.MemoryRevision);
        await coordinator.DisposeAsync();
    }

    [TestMethod]
    public async Task ConnectionSnapshotsDoNotDuplicateDedicatedMemorySamples()
    {
        (AppSessionCoordinator coordinator, FakeBackendSession session) =
            await CreateCoordinatorAsync();
        using OverviewViewModel viewModel = new(coordinator);
        session.Streams.Memory.Publish(new ClashMemorySample { InUse = 96 * 1024 * 1024 });
        await WaitUntilAsync(() => viewModel.MemoryHistory.Count == 1);
        long memoryRevision = viewModel.MemoryRevision;

        await Task.Delay(300);
        session.Streams.Connections.Publish(new ConnectionStreamSnapshot
        {
            Memory = 32 * 1024 * 1024,
            Connections = [Connection("stable", "example.com")],
        });
        await WaitUntilAsync(() => viewModel.RecentConnections.Count == 1);

        Assert.HasCount(1, viewModel.MemoryHistory);
        Assert.AreEqual(96 * 1024 * 1024, viewModel.MemoryHistory[0].InUse);
        Assert.AreEqual(memoryRevision, viewModel.MemoryRevision);
        await coordinator.DisposeAsync();
    }

    [TestMethod]
    public async Task ZeroMemorySampleFallsBackToPositiveConnectionMemory()
    {
        (AppSessionCoordinator coordinator, FakeBackendSession session) =
            await CreateCoordinatorAsync();
        using OverviewViewModel viewModel = new(coordinator);
        const long connectionMemory = 80L * 1024 * 1024;
        session.Streams.Connections.Publish(new ConnectionStreamSnapshot
        {
            Memory = connectionMemory,
            Connections = [Connection("stable", "example.com")],
        });
        await WaitUntilAsync(() => viewModel.MemoryHistory.Count == 1);

        long previousRevision = viewModel.MemoryRevision;
        session.Streams.Memory.Publish(new ClashMemorySample { InUse = 0 });
        await WaitUntilAsync(() => viewModel.MemoryRevision > previousRevision);

        Assert.AreEqual(connectionMemory, viewModel.MemoryHistory[^1].InUse);
        Assert.IsTrue(viewModel.MemoryHistory.All(static point => point.InUse > 0));
        await coordinator.DisposeAsync();
    }

    private static async Task<(AppSessionCoordinator Coordinator, FakeBackendSession Session)>
        CreateCoordinatorAsync()
    {
        BackendProfile profile = new(
            Guid.NewGuid(),
            "Overview backend",
            BackendEndpoint.Create("http://127.0.0.1:9090"));
        FakeBackendProfileStore profiles = new()
        {
            Current = new BackendProfileSet
            {
                Profiles = [profile],
                ActiveProfileId = profile.Id,
            },
        };
        FakeBackendSession? session = null;
        FakeBackendSessionFactory sessions = new()
        {
            CreateHandler = (createdProfile, _, epoch, _) =>
            {
                session = new FakeBackendSession(epoch, createdProfile);
                return ValueTask.FromResult<IBackendSession>(session);
            },
        };
        AppSessionCoordinator coordinator = new(
            profiles,
            new FakeBackendCredentialStore(),
            sessions,
            new InlineTestDispatcher(),
            new AppSettingsState(null),
            TimeProvider.System);
        await coordinator.RunUserOperationAsync(coordinator.InitializeAsync);
        FakeBackendSession createdSession = session
            ?? throw new InvalidOperationException("The test session was not created.");
        await createdSession.Streams.AllStarted.WaitAsync(TestTimeout);
        return (coordinator, createdSession);
    }

    private static ClashConnection Connection(string id, string host) => new()
    {
        Id = id,
        StartedAt = DateTimeOffset.UnixEpoch,
        Rule = "MATCH",
        Chains = ["DIRECT"],
        Metadata = new ClashConnectionMetadata
        {
            Host = host,
            Network = "tcp",
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
                Assert.Fail("The expected overview state was not observed before the timeout.");
            }

            await Task.Delay(10);
        }
    }
}
