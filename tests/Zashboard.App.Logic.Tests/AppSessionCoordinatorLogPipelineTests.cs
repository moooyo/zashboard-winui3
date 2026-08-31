using System.Collections.Concurrent;
using Zashboard.App.Services;
using Zashboard.Core.Backends;
using Zashboard.Core.Clash;
using Zashboard.Infrastructure.Persistence;

namespace Zashboard.App.Logic.Tests;

[TestClass]
public sealed class AppSessionCoordinatorLogPipelineTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task TransportAndApplicationDropsAreMergedThroughTheActiveSession()
    {
        ManualTimeProvider timeProvider = new();
        FakeBackendSessionFactory sessions = new();
        await using AppSessionCoordinator coordinator = CreateCoordinator(
            CreateProfileStore(CreateProfile()),
            sessions,
            new InlineTestDispatcher(),
            timeProvider);
        await coordinator.RunUserOperationAsync(coordinator.InitializeAsync);
        FakeBackendSession session = (FakeBackendSession)sessions.CreatedSessions.Single();
        await session.Streams.AllStarted.WaitAsync(TestTimeout);
        ConcurrentQueue<SessionLogsChangedEventArgs> changes = new();
        coordinator.LogsChanged += (_, args) => changes.Enqueue(args);

        session.Streams.RaiseDropped(ClashStreamKind.Logs, 7);
        for (int index = 0; index < 4_096; index++)
        {
            session.Streams.PublishLog(Message($"entry {index}"));
        }

        await WaitUntilAsync(() => session.Streams.Logs.DeliveredCount == 4_096);
        timeProvider.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => coordinator.DroppedLogCount == 2_055);

        Assert.AreEqual(2_055L, coordinator.DroppedLogCount);
        SessionLogsChangedEventArgs merged = changes.Single(args =>
            args.DroppedBeforeDisplay == 2_055);
        Assert.HasCount(256, merged.Added);
        Assert.HasCount(256, coordinator.Logs);
    }

    [TestMethod]
    public async Task DroppedEventsFromAReplacedSessionAreIgnored()
    {
        BackendProfile profile = CreateProfile();
        ManualTimeProvider timeProvider = new();
        FakeBackendSessionFactory sessions = new();
        await using AppSessionCoordinator coordinator = CreateCoordinator(
            CreateProfileStore(profile),
            sessions,
            new InlineTestDispatcher(),
            timeProvider);
        await coordinator.RunUserOperationAsync(coordinator.InitializeAsync);
        FakeBackendSession oldSession = (FakeBackendSession)sessions.CreatedSessions.Single();
        await oldSession.Streams.AllStarted.WaitAsync(TestTimeout);

        await coordinator.RunUserOperationAsync(coordinator.ReconnectAsync);
        FakeBackendSession activeSession = (FakeBackendSession)sessions.CreatedSessions.Last();
        await activeSession.Streams.AllStarted.WaitAsync(TestTimeout);
        oldSession.Streams.RaiseDropped(ClashStreamKind.Logs, 11);
        activeSession.Streams.RaiseDropped(ClashStreamKind.Logs, 3);

        timeProvider.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => coordinator.DroppedLogCount == 3);

        Assert.AreEqual(3L, coordinator.DroppedLogCount);
        Assert.AreSame(activeSession, coordinator.ActiveSession);
    }

    [TestMethod]
    public async Task DroppedOnlyTailIsFlushedWhenTheLogStreamCompletes()
    {
        ManualTimeProvider timeProvider = new();
        QueuedTestDispatcher dispatcher = new();
        FakeBackendSessionFactory sessions = new();
        await using AppSessionCoordinator coordinator = CreateCoordinator(
            CreateProfileStore(CreateProfile()),
            sessions,
            dispatcher,
            timeProvider);
        await coordinator.RunUserOperationAsync(coordinator.InitializeAsync);
        FakeBackendSession session = (FakeBackendSession)sessions.CreatedSessions.Single();
        await session.Streams.AllStarted.WaitAsync(TestTimeout);
        ConcurrentQueue<SessionLogsChangedEventArgs> changes = new();
        coordinator.LogsChanged += (_, args) => changes.Enqueue(args);
        session.Streams.PublishLog(Message("visible"));
        await WaitUntilAsync(() => session.Streams.Logs.DeliveredCount == 1);

        dispatcher.HoldNextInvocation();
        timeProvider.Advance(TimeSpan.FromMilliseconds(100));
        await dispatcher.NextInvocationQueued.WaitAsync(TestTimeout);
        session.Streams.RaiseDropped(ClashStreamKind.Logs, 5);
        session.Streams.Logs.Complete();
        await session.Streams.Logs.Completed.Task.WaitAsync(TestTimeout);
        await Task.Delay(50);
        dispatcher.ReleaseNext();
        await WaitUntilAsync(() => coordinator.DroppedLogCount == 5);

        SessionLogsChangedEventArgs tail = changes.Single(args =>
            args.DroppedBeforeDisplay == 5);
        Assert.IsEmpty(tail.Added);
        Assert.AreEqual(5L, coordinator.DroppedLogCount);
    }

    [TestMethod]
    public async Task ClearRestartsOnlyTheLogIterationAndRejectsItsQueuedFrames()
    {
        ManualTimeProvider timeProvider = new();
        FakeBackendSessionFactory sessions = new();
        await using AppSessionCoordinator coordinator = CreateCoordinator(
            CreateProfileStore(CreateProfile()),
            sessions,
            new InlineTestDispatcher(),
            timeProvider);
        await coordinator.RunUserOperationAsync(coordinator.InitializeAsync);
        FakeBackendSession session = (FakeBackendSession)sessions.CreatedSessions.Single();
        await session.Streams.AllStarted.WaitAsync(TestTimeout);
        session.Streams.Logs.PauseDelivery();
        session.Streams.PublishLog(Message("before clear"));
        await session.Streams.Logs.DeliveryBlocked.WaitAsync(TestTimeout);

        coordinator.ClearLogs();
        await WaitUntilAsync(() => session.Streams.Logs.StartedCount == 2);
        session.Streams.Logs.ResumeDelivery();
        session.Streams.PublishLog(Message("after clear"));
        await WaitUntilAsync(() => session.Streams.Logs.DeliveredCount == 1);
        timeProvider.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => coordinator.Logs.Count == 1);

        Assert.AreEqual(2, session.Streams.Logs.StartedCount);
        Assert.AreEqual(1, session.Streams.Connections.StartedCount);
        Assert.AreEqual(1, session.Streams.Traffic.StartedCount);
        Assert.AreEqual(1, session.Streams.Memory.StartedCount);
        Assert.HasCount(1, coordinator.Logs);
        Assert.AreEqual("after clear", coordinator.Logs[0].Message);
    }

    private static AppSessionCoordinator CreateCoordinator(
        FakeBackendProfileStore profiles,
        FakeBackendSessionFactory sessions,
        IUiDispatcher dispatcher,
        TimeProvider timeProvider) => new(
        profiles,
        new FakeBackendCredentialStore(),
        sessions,
        dispatcher,
        new AppSettingsState(),
        timeProvider);

    private static FakeBackendProfileStore CreateProfileStore(BackendProfile profile) => new()
    {
        Current = new BackendProfileSet
        {
            Profiles = [profile],
            ActiveProfileId = profile.Id,
        },
    };

    private static BackendProfile CreateProfile() => new(
        Guid.NewGuid(),
        "Log backend",
        BackendEndpoint.Create("http://127.0.0.1:9090"));

    private static ClashLogMessage Message(string payload) => new()
    {
        Level = ClashLogLevel.Info,
        RawLevel = "info",
        Payload = payload,
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TestTimeout);
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
