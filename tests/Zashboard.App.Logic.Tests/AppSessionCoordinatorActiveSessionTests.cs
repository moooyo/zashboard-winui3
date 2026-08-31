using Zashboard.App.Services;
using Zashboard.Core.Backends;
using Zashboard.Core.Sessions;
using Zashboard.Infrastructure.Persistence;

namespace Zashboard.App.Logic.Tests;

[TestClass]
public sealed class AppSessionCoordinatorActiveSessionTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task ActiveSessionInitializationPublishesSessionAndDisposeWaitsForStreams()
    {
        BackendProfile profile = CreateProfile();
        FakeBackendProfileStore profiles = CreateActiveProfileStore(profile);
        FakeBackendSessionFactory sessions = new()
        {
            CreateHandler = (createdProfile, _, epoch, _) =>
                ValueTask.FromResult<IBackendSession>(new FakeBackendSession(
                    epoch,
                    createdProfile,
                    holdStreamsAfterCancellation: true)),
        };
        AppSessionCoordinator coordinator = CreateCoordinator(profiles, sessions);

        await coordinator.RunUserOperationAsync(coordinator.InitializeAsync);
        FakeBackendSession session = (FakeBackendSession)sessions.CreatedSessions.Single();
        await session.Streams.AllStarted.WaitAsync(TestTimeout);

        Assert.IsTrue(coordinator.IsInitialized);
        Assert.AreSame(session, coordinator.ActiveSession);
        Assert.AreEqual(profile.Id, coordinator.ActiveProfile?.Id);
        Assert.AreEqual(BackendConnectionState.Online, coordinator.SessionSnapshot.State);

        Task disposal = coordinator.DisposeAsync().AsTask();
        await Task.WhenAll(
            session.Streams.AllCancellationObserved,
            session.DisposeStarted.Task).WaitAsync(TestTimeout);

        Assert.IsFalse(disposal.IsCompleted);
        session.Streams.ReleaseAll();
        await Task.WhenAll(disposal, session.Streams.AllCompleted).WaitAsync(TestTimeout);

        Assert.AreEqual(1, session.DisposeCount);
        Assert.IsNull(coordinator.ActiveSession);
    }

    [TestMethod]
    public async Task ReconnectCancellationWhileFactoryWaitsPublishesOfflineRetrying()
    {
        BackendProfile profile = CreateProfile();
        FakeBackendProfileStore profiles = CreateActiveProfileStore(profile);
        TaskCompletionSource secondCreateStarted = NewCompletion();
        int createCount = 0;
        FakeBackendSessionFactory sessions = new()
        {
            CreateHandler = async (createdProfile, _, epoch, cancellationToken) =>
            {
                if (Interlocked.Increment(ref createCount) == 1)
                {
                    return new FakeBackendSession(epoch, createdProfile);
                }

                secondCreateStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                    .ConfigureAwait(false);
                throw new InvalidOperationException("The reconnect factory unexpectedly completed.");
            },
        };
        AppSessionCoordinator coordinator = CreateCoordinator(profiles, sessions);
        await coordinator.RunUserOperationAsync(coordinator.InitializeAsync);
        FakeBackendSession initialSession = (FakeBackendSession)sessions.CreatedSessions.Single();
        await initialSession.Streams.AllStarted.WaitAsync(TestTimeout);
        using CancellationTokenSource cancellationSource = new();

        Task reconnect = coordinator.RunUserOperationAsync(
            token => coordinator.ReconnectAsync(token),
            cancellationSource.Token);
        await secondCreateStarted.Task.WaitAsync(TestTimeout);
        await cancellationSource.CancelAsync();

        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => reconnect);

        Assert.HasCount(2, sessions.Calls);
        Assert.AreEqual(1, initialSession.DisposeCount);
        Assert.IsNull(coordinator.ActiveSession);
        Assert.AreEqual(profile.Id, coordinator.ActiveProfile?.Id);
        Assert.AreEqual(BackendConnectionState.OfflineRetrying, coordinator.SessionSnapshot.State);
        StringAssert.Contains(
            coordinator.SessionSnapshot.StatusDetail ?? string.Empty,
            "canceled");

        await coordinator.DisposeAsync();
    }

    [TestMethod]
    public async Task ReconnectCancellationDisposesFactorySessionReturnedAfterCancellation()
    {
        BackendProfile profile = CreateProfile();
        FakeBackendProfileStore profiles = CreateActiveProfileStore(profile);
        TaskCompletionSource releaseSecondCreate = NewCompletion();
        TaskCompletionSource<FakeBackendSession> lateSessionCreated =
            NewCompletion<FakeBackendSession>();
        int createCount = 0;
        FakeBackendSessionFactory sessions = new()
        {
            CreateHandler = async (createdProfile, _, epoch, _) =>
            {
                if (Interlocked.Increment(ref createCount) == 1)
                {
                    return new FakeBackendSession(epoch, createdProfile);
                }

                FakeBackendSession lateSession = new(epoch, createdProfile);
                lateSessionCreated.TrySetResult(lateSession);
                await releaseSecondCreate.Task.ConfigureAwait(false);
                return lateSession;
            },
        };
        AppSessionCoordinator coordinator = CreateCoordinator(profiles, sessions);
        await coordinator.RunUserOperationAsync(coordinator.InitializeAsync);
        FakeBackendSession initialSession = (FakeBackendSession)sessions.CreatedSessions.Single();
        await initialSession.Streams.AllStarted.WaitAsync(TestTimeout);
        using CancellationTokenSource cancellationSource = new();

        Task reconnect = coordinator.RunUserOperationAsync(
            token => coordinator.ReconnectAsync(token),
            cancellationSource.Token);
        FakeBackendSession lateSession = await lateSessionCreated.Task.WaitAsync(TestTimeout);
        await cancellationSource.CancelAsync();

        Assert.IsFalse(reconnect.IsCompleted);
        releaseSecondCreate.TrySetResult();
        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => reconnect);
        await lateSession.DisposeStarted.Task.WaitAsync(TestTimeout);

        Assert.AreEqual(1, initialSession.DisposeCount);
        Assert.AreEqual(1, lateSession.DisposeCount);
        Assert.IsNull(coordinator.ActiveSession);
        Assert.AreEqual(BackendConnectionState.OfflineRetrying, coordinator.SessionSnapshot.State);

        await coordinator.DisposeAsync();
    }

    [TestMethod]
    public async Task SessionDisposeFailureStillCancelsAndCompletesBackgroundStreams()
    {
        BackendProfile profile = CreateProfile();
        FakeBackendProfileStore profiles = CreateActiveProfileStore(profile);
        FakeBackendSessionFactory sessions = new()
        {
            CreateHandler = (createdProfile, _, epoch, _) =>
                ValueTask.FromResult<IBackendSession>(new FakeBackendSession(
                    epoch,
                    createdProfile,
                    holdStreamsAfterCancellation: true,
                    disposeException: new InvalidOperationException("Session disposal failed."))),
        };
        AppSessionCoordinator coordinator = CreateCoordinator(profiles, sessions);
        await coordinator.RunUserOperationAsync(coordinator.InitializeAsync);
        FakeBackendSession session = (FakeBackendSession)sessions.CreatedSessions.Single();
        await session.Streams.AllStarted.WaitAsync(TestTimeout);

        Task disposal = coordinator.DisposeAsync().AsTask();
        await Task.WhenAll(
            session.Streams.AllCancellationObserved,
            session.DisposeStarted.Task).WaitAsync(TestTimeout);

        Assert.IsFalse(disposal.IsCompleted);
        session.Streams.ReleaseAll();
        await session.Streams.AllCompleted.WaitAsync(TestTimeout);
        InvalidOperationException exception =
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => disposal);

        Assert.AreEqual("Session disposal failed.", exception.Message);
        Assert.AreEqual(1, session.DisposeCount);
        Assert.IsNull(coordinator.ActiveSession);
    }

    private static AppSessionCoordinator CreateCoordinator(
        FakeBackendProfileStore profiles,
        IBackendSessionFactory sessions) => new(
        profiles,
        new FakeBackendCredentialStore(),
        sessions,
        new InlineTestDispatcher(),
        new AppSettingsState(),
        TimeProvider.System);

    private static FakeBackendProfileStore CreateActiveProfileStore(BackendProfile profile) => new()
    {
        Current = new BackendProfileSet
        {
            Profiles = [profile],
            ActiveProfileId = profile.Id,
        },
    };

    private static BackendProfile CreateProfile() => new(
        Guid.NewGuid(),
        "Active backend",
        BackendEndpoint.Create("http://127.0.0.1:9090"));

    private static TaskCompletionSource NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<T> NewCompletion<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
