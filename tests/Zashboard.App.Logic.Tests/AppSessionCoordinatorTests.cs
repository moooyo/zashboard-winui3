using Zashboard.App.Services;
using Zashboard.Core.Backends;
using Zashboard.Core.Clash;
using Zashboard.Core.Sessions;
using Zashboard.Infrastructure.Persistence;

namespace Zashboard.App.Logic.Tests;

[TestClass]
public sealed class AppSessionCoordinatorTests
{
    [TestMethod]
    public void BackendSaveRequestToStringRedactsSecret()
    {
        BackendSaveRequest request = new(
            null,
            "Backend",
            new Uri("https://controller.example"),
            "highly-sensitive-secret",
            BackendCredentialUpdate.Replace);

        string formatted = request.ToString();

        StringAssert.Contains(formatted, "<redacted>");
        Assert.IsFalse(formatted.Contains("highly-sensitive-secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DisposeCancelsRunningAndQueuedUserOperationsAndJoinsConcurrentDispose()
    {
        AppSessionCoordinator coordinator = CreateCoordinator();
        TaskCompletionSource entered = NewCompletion();
        TaskCompletionSource cancellationObserved = NewCompletion();
        TaskCompletionSource allowExit = NewCompletion();
        int queuedEntryCount = 0;

        Task running = coordinator.RunUserOperationAsync(async token =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                cancellationObserved.TrySetResult();
                await allowExit.Task;
            }
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task queued = coordinator.RunUserOperationAsync(_ =>
        {
            Interlocked.Increment(ref queuedEntryCount);
            return Task.CompletedTask;
        });
        Task firstDispose = coordinator.DisposeAsync().AsTask();
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task secondDispose = coordinator.DisposeAsync().AsTask();

        Assert.IsFalse(firstDispose.IsCompleted);
        Assert.IsFalse(secondDispose.IsCompleted);
        allowExit.TrySetResult();

        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => running);
        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => queued);
        await Task.WhenAll(firstDispose, secondDispose).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(0, queuedEntryCount);
        Assert.IsFalse(coordinator.IsUserOperationRunning);
        _ = await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() =>
            coordinator.RunUserOperationAsync(_ => Task.CompletedTask));
    }

    [TestMethod]
    public async Task InitializationCompletesBeforeQueuedProfileSaveUsesLoadedState()
    {
        BackendProfile existing = CreateProfile(
            "Existing",
            "http://127.0.0.1:9090");
        TaskCompletionSource loadStarted = NewCompletion();
        TaskCompletionSource<BackendProfileSet> releaseLoad = NewCompletion<BackendProfileSet>();
        RecordingProfileStore profiles = new()
        {
            LoadAsyncHandler = async token =>
            {
                loadStarted.TrySetResult();
                return await releaseLoad.Task.WaitAsync(token);
            },
        };
        ThrowingSessionFactory sessions = new();
        AppSessionCoordinator coordinator = CreateCoordinator(
            profiles,
            new RecordingCredentialStore(),
            sessions);

        Task initialize = coordinator.RunUserOperationAsync(coordinator.InitializeAsync);
        await loadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task save = coordinator.RunUserOperationAsync(token =>
            coordinator.SaveAndConnectAsync(
                new BackendSaveRequest(
                    null,
                    "Second",
                    new Uri("http://127.0.0.1:9091"),
                    string.Empty,
                    BackendCredentialUpdate.Remove),
                token));

        releaseLoad.TrySetResult(new BackendProfileSet { Profiles = [existing] });
        await Task.WhenAll(initialize, save).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.HasCount(2, profiles.Current.Profiles);
        Assert.HasCount(2, coordinator.Profiles);
        Assert.AreEqual("Existing", profiles.Current.Profiles[0].Name);
        Assert.AreEqual("Second", profiles.Current.Profiles[1].Name);
        Assert.AreEqual(profiles.Current.Profiles[1].Id, profiles.Current.ActiveProfileId);
        Assert.AreEqual(1, sessions.CreateCount);

        await coordinator.DisposeAsync();
    }

    [TestMethod]
    public async Task CredentialCommitFailureRollsBackProfileAndSkipsSessionCreation()
    {
        BackendProfile existing = CreateProfile(
            "Existing",
            "http://127.0.0.1:9090");
        RecordingProfileStore profiles = new()
        {
            Current = new BackendProfileSet { Profiles = [existing] },
        };
        RecordingCredentialStore credentials = new()
        {
            SetException = new IOException("Credential write failed."),
        };
        ThrowingSessionFactory sessions = new();
        AppSessionCoordinator coordinator = CreateCoordinator(profiles, credentials, sessions);
        await coordinator.RunUserOperationAsync(coordinator.InitializeAsync);

        IOException exception = await Assert.ThrowsExactlyAsync<IOException>(() =>
            coordinator.RunUserOperationAsync(token =>
                coordinator.SaveAndConnectAsync(
                    new BackendSaveRequest(
                        null,
                        "Second",
                        new Uri("http://127.0.0.1:9091"),
                        "new-secret",
                        BackendCredentialUpdate.Replace),
                    token)));

        Assert.AreEqual("Credential write failed.", exception.Message);
        Assert.HasCount(2, profiles.Saves);
        Assert.AreEqual(existing.Id, profiles.Current.Profiles.Single().Id);
        Assert.HasCount(1, coordinator.Profiles);
        Assert.AreEqual(existing.Id, coordinator.Profiles[0].Id);
        Assert.AreEqual(0, sessions.CreateCount);

        await coordinator.DisposeAsync();
    }

    [TestMethod]
    public async Task CreateReturnsStableIdentityUsedBySubsequentUpdate()
    {
        RecordingProfileStore profiles = new();
        AppSessionCoordinator coordinator = CreateCoordinator(profiles: profiles);
        await coordinator.RunUserOperationAsync(coordinator.InitializeAsync);
        Guid createdId = Guid.Empty;

        await coordinator.RunUserOperationAsync(async token =>
        {
            createdId = await coordinator.SaveAndConnectAsync(
                new BackendSaveRequest(
                    null,
                    "Backend",
                    new Uri("http://127.0.0.1:9090"),
                    string.Empty,
                    BackendCredentialUpdate.Remove),
                token);
        });
        await coordinator.RunUserOperationAsync(async token =>
        {
            Guid updatedId = await coordinator.SaveAndConnectAsync(
                new BackendSaveRequest(
                    createdId,
                    "Renamed backend",
                    new Uri("http://127.0.0.1:9090"),
                    string.Empty,
                    BackendCredentialUpdate.Keep),
                token);
            Assert.AreEqual(createdId, updatedId);
        });

        Assert.AreNotEqual(Guid.Empty, createdId);
        Assert.HasCount(1, profiles.Current.Profiles);
        Assert.AreEqual(createdId, profiles.Current.Profiles[0].Id);
        Assert.AreEqual("Renamed backend", profiles.Current.Profiles[0].Name);
        Assert.HasCount(1, coordinator.Profiles);

        await coordinator.DisposeAsync();
    }

    [TestMethod]
    public async Task UserOperationFailureIsPublishedGloballyAndClearedByNextOperation()
    {
        AppSessionCoordinator coordinator = CreateCoordinator();

        InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => coordinator.RunUserOperationAsync(_ =>
                Task.FromException(new InvalidOperationException("Operation failed."))));

        Assert.AreEqual("Operation failed.", exception.Message);
        Assert.AreEqual("Operation failed.", coordinator.LastUserOperationErrorMessage);
        Assert.IsFalse(coordinator.IsUserOperationRunning);

        await coordinator.RunUserOperationAsync(_ => Task.CompletedTask);

        Assert.IsNull(coordinator.LastUserOperationErrorMessage);
        await coordinator.DisposeAsync();
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(65_536)]
    public async Task ConfigurationPatchRejectsInvalidListenerPort(int port)
    {
        AppSessionCoordinator coordinator = CreateCoordinator();

        _ = await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            coordinator.PatchConfigurationAsync(new ClashConfigurationPatch { Port = port }));

        await coordinator.DisposeAsync();
    }

    [TestMethod]
    public async Task ConfigurationPatchRejectsEmptyUpdate()
    {
        AppSessionCoordinator coordinator = CreateCoordinator();

        _ = await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            coordinator.PatchConfigurationAsync(new ClashConfigurationPatch()));

        await coordinator.DisposeAsync();
    }

    private static AppSessionCoordinator CreateCoordinator(
        RecordingProfileStore? profiles = null,
        RecordingCredentialStore? credentials = null,
        IBackendSessionFactory? sessions = null) => new(
        profiles ?? new RecordingProfileStore(),
        credentials ?? new RecordingCredentialStore(),
        sessions ?? new ThrowingSessionFactory(),
        new InlineDispatcher(),
        new AppSettingsState(),
        TimeProvider.System);

    private static BackendProfile CreateProfile(string name, string endpoint) => new(
        Guid.NewGuid(),
        name,
        BackendEndpoint.Create(endpoint));

    private static TaskCompletionSource NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<T> NewCompletion<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class InlineDispatcher : IUiDispatcher
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

    private sealed class RecordingProfileStore : IBackendProfileStore
    {
        public Func<CancellationToken, Task<BackendProfileSet>>? LoadAsyncHandler { get; init; }

        public BackendProfileSet Current { get; set; } = new();

        public List<BackendProfileSet> Saves { get; } = [];

        public async ValueTask<BackendProfileSet> LoadAsync(
            CancellationToken cancellationToken = default) =>
            LoadAsyncHandler is null
                ? Current
                : await LoadAsyncHandler(cancellationToken);

        public ValueTask SaveAsync(
            BackendProfileSet profiles,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Current = profiles;
            Saves.Add(profiles);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingCredentialStore : IBackendCredentialStore
    {
        public Exception? SetException { get; init; }

        public ValueTask<bool> ExistsAsync(
            Guid profileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(false);
        }

        public ValueTask<BackendCredential?> GetAsync(
            Guid profileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<BackendCredential?>(null);
        }

        public ValueTask SetAsync(
            Guid profileId,
            BackendCredential credential,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SetException is not null)
            {
                throw SetException;
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(
            Guid profileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingSessionFactory : IBackendSessionFactory
    {
        public int CreateCount { get; private set; }

        public ValueTask<IBackendSession> CreateAsync(
            BackendProfile profile,
            BackendCredential credential,
            SessionEpoch epoch,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCount++;
            throw new InvalidOperationException("Session creation is intentionally unavailable.");
        }
    }
}
