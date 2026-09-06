using System.Net;
using Zashboard.App.Services;
using Zashboard.Core.Backends;
using Zashboard.Core.Clash;
using Zashboard.Core.Sessions;
using Zashboard.Infrastructure.Clash;
using Zashboard.Infrastructure.Persistence;

namespace Zashboard.App.Logic.Tests
{
    [TestClass]
    public sealed class AppSessionCoordinatorRecoveryTests
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task LateInitialResultCannotReplaceOrDegradeNewerRefresh(bool initialFails)
        {
            TaskCompletionSource<ProxyCatalog> initialResponse = NewCompletion<ProxyCatalog>();
            int calls = 0;
            await using AppSessionCoordinator coordinator = CreateCoordinator(session =>
            {
                session.Rest.GetProxiesHandler = token => Interlocked.Increment(ref calls) == 1
                    ? initialResponse.Task.WaitAsync(token)
                    : Task.FromResult(Catalog("Refreshed"));
            });
            await coordinator.InitializeAsync();
            Task initialLoad = coordinator.InitialResourceLoadForRecoveryTest;
            Assert.AreEqual(1, calls);

            await coordinator.RunUserOperationAsync(coordinator.RefreshAsync);
            Assert.AreEqual("Refreshed", coordinator.ProxyCatalog?.Proxies.Keys.Single());

            if (initialFails)
            {
                initialResponse.SetException(new ClashApiException("The initial request failed."));
            }
            else
            {
                initialResponse.SetResult(Catalog("Initial"));
            }

            await initialLoad.WaitAsync(TestTimeout);

            Assert.AreEqual("Refreshed", coordinator.ProxyCatalog?.Proxies.Keys.Single());
            Assert.AreEqual(BackendConnectionState.Online, coordinator.SessionSnapshot.State);
            Assert.IsNull(coordinator.LastErrorMessage);
            Assert.AreEqual(2, calls);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task LateOptionalResourceAuthenticationFailureCannotInvalidateNewerSuccess(
            bool runtimeStatistics)
        {
            TaskCompletionSource releaseInitial = NewCompletion();
            int calls = 0;
            await using AppSessionCoordinator coordinator = CreateCoordinator(session =>
            {
                session.Rest.ProxiesResult = new ProxyCatalog
                {
                    Proxies = new Dictionary<string, ClashProxy>(StringComparer.Ordinal)
                    {
                        ["Smart"] = new ClashProxy
                        {
                            Name = "Smart",
                            Type = "Smart",
                            Kind = ClashProxyKind.Smart,
                        },
                    },
                };
                if (runtimeStatistics)
                {
                    session.Rest.GetHonkRuntimeStatisticsHandler = async token =>
                    {
                        if (Interlocked.Increment(ref calls) == 1)
                        {
                            await releaseInitial.Task.WaitAsync(token);
                            throw new ClashAuthenticationException(null);
                        }

                        return new HonkRuntimeStatistics
                        {
                            Outbounds = [new HonkOutboundStatistics { Name = "Refreshed" }],
                        };
                    };
                }
                else
                {
                    session.Rest.GetSmartWeightsHandler = async token =>
                    {
                        if (Interlocked.Increment(ref calls) == 1)
                        {
                            await releaseInitial.Task.WaitAsync(token);
                            throw new ClashAuthenticationException(null);
                        }

                        return new SmartWeights { Message = "Refreshed" };
                    };
                }
            }, new ManualTimeProvider());
            await coordinator.InitializeAsync();
            Task initialLoad = coordinator.InitialResourceLoadForRecoveryTest;
            Assert.AreEqual(1, calls);

            if (runtimeStatistics)
            {
                await coordinator.RunUserOperationAsync(coordinator.RefreshHonkRuntimeStatisticsAsync);
            }
            else
            {
                await coordinator.RunUserOperationAsync(coordinator.RefreshSmartWeightsAsync);
            }

            releaseInitial.SetResult();
            await initialLoad.WaitAsync(TestTimeout);

            string? displayedValue = runtimeStatistics
                ? coordinator.HonkRuntimeStatistics?.Outbounds.Single().Name
                : coordinator.SmartWeights?.Message;
            Assert.AreEqual("Refreshed", displayedValue);
            Assert.AreEqual(BackendConnectionState.Online, coordinator.SessionSnapshot.State);
            Assert.IsNull(coordinator.LastErrorMessage);
            Assert.IsNull(coordinator.LastUserOperationErrorMessage);
            Assert.AreEqual(2, calls);
        }

        [TestMethod]
        public async Task CancelingNewerRefreshRetriesResourceWhoseInitialResponseBecameStale()
        {
            TrackingTimeProvider timeProvider = new();
            TaskCompletionSource<ProxyCatalog> initialResponse = NewCompletion<ProxyCatalog>();
            TaskCompletionSource refreshEntered = NewCompletion();
            int calls = 0;
            await using AppSessionCoordinator coordinator = CreateCoordinator(session =>
            {
                session.Rest.GetProxiesHandler = async token =>
                {
                    int call = Interlocked.Increment(ref calls);
                    if (call == 1)
                    {
                        return await initialResponse.Task.WaitAsync(token);
                    }

                    if (call == 2)
                    {
                        refreshEntered.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }

                    return Catalog("Recovered after cancellation");
                };
            }, timeProvider);
            await coordinator.InitializeAsync();
            Task initialLoad = coordinator.InitialResourceLoadForRecoveryTest;
            Task refresh = coordinator.RunUserOperationAsync(coordinator.RefreshAsync);
            await refreshEntered.Task.WaitAsync(TestTimeout);

            coordinator.CancelUserOperation();
            await CompleteCanceledOperationAsync(refresh);
            Assert.IsNull(coordinator.ProxyCatalog);
            initialResponse.SetResult(Catalog("Stale initial response"));
            await initialLoad.WaitAsync(TestTimeout);
            Assert.IsNull(coordinator.ProxyCatalog);
            await WaitUntilAsync(() => timeProvider.TimerCount(TimeSpan.FromSeconds(2)) == 1);

            timeProvider.Advance(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => timeProvider.TimerCount(TimeSpan.FromSeconds(2)) >= 2);

            Assert.AreEqual(3, calls);
            Assert.AreEqual("Recovered after cancellation", coordinator.ProxyCatalog?.Proxies.Keys.Single());
            Assert.AreEqual(BackendConnectionState.Online, coordinator.SessionSnapshot.State);
            Assert.IsNull(coordinator.LastErrorMessage);
        }

        [TestMethod]
        [DataRow("configuration")]
        [DataRow("proxies")]
        [DataRow("proxy providers")]
        [DataRow("rules")]
        [DataRow("rule providers")]
        public async Task InitialTransientResourceFailureRecoversWithoutManualRefresh(string resource)
        {
            TrackingTimeProvider timeProvider = new();
            int calls = 0;
            await using AppSessionCoordinator coordinator = CreateCoordinator(session =>
            {
                SetResourceHandler(session.Rest, resource, () =>
                    Interlocked.Increment(ref calls) == 1
                        ? new ClashApiException("The controller is starting.", HttpStatusCode.ServiceUnavailable)
                        : null);
            }, timeProvider);
            await coordinator.InitializeAsync();
            await coordinator.InitialResourceLoadForRecoveryTest.WaitAsync(TestTimeout);
            FakeBackendSession session = (FakeBackendSession)coordinator.ActiveSession!;
            PublishConnectedStreams(session, timeProvider.GetUtcNow());

            Assert.AreEqual(1, calls);
            Assert.AreEqual(BackendConnectionState.Degraded, coordinator.SessionSnapshot.State);
            await WaitUntilAsync(() => timeProvider.TimerCount(TimeSpan.FromSeconds(2)) == 1);

            timeProvider.Advance(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => timeProvider.TimerCount(TimeSpan.FromSeconds(2)) >= 2);

            Assert.AreEqual(2, calls);
            Assert.IsNotNull(coordinator.Configuration);
            Assert.IsNotNull(coordinator.ProxyCatalog);
            Assert.IsNotNull(coordinator.ProxyProviders);
            Assert.IsNotNull(coordinator.RuleCatalog);
            Assert.IsNotNull(coordinator.RuleProviders);
            Assert.AreEqual(BackendConnectionState.Online, coordinator.SessionSnapshot.State);
            Assert.IsNull(coordinator.LastErrorMessage);

            timeProvider.Advance(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => timeProvider.TimerCount(TimeSpan.FromSeconds(2)) >= 3);
            Assert.AreEqual(2, calls, "A recovered resource must not be polled as a failed resource.");
        }

        [TestMethod]
        public async Task VersionHandshakeRetriesAndPublishesRecoveredVersion()
        {
            TrackingTimeProvider timeProvider = new();
            int versionCalls = 0;
            await using AppSessionCoordinator coordinator = CreateCoordinator(session =>
            {
                session.Rest.GetVersionHandler = _ => Interlocked.Increment(ref versionCalls) == 1
                    ? Task.FromException<ClashVersion>(new ClashApiException("The controller is starting."))
                    : Task.FromResult(new ClashVersion
                    {
                        Value = "Mihomo Meta recovered",
                        CoreKind = ClashCoreKind.Mihomo,
                    });
            }, timeProvider, recoveringVersion: true);
            await coordinator.InitializeAsync();
            await coordinator.InitialResourceLoadForRecoveryTest.WaitAsync(TestTimeout);
            await WaitUntilAsync(() => timeProvider.TimerCount(TimeSpan.FromSeconds(2)) == 1);

            timeProvider.Advance(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => timeProvider.TimerCount(TimeSpan.FromSeconds(4)) == 1);
            Assert.AreEqual(1, versionCalls);
            Assert.AreEqual(string.Empty, coordinator.SessionSnapshot.Version);

            timeProvider.Advance(TimeSpan.FromSeconds(4));
            await WaitUntilAsync(() => timeProvider.TimerCount(TimeSpan.FromSeconds(2)) >= 2);

            Assert.AreEqual(2, versionCalls);
            Assert.AreEqual("Mihomo Meta recovered", coordinator.SessionSnapshot.Version);
            Assert.AreEqual(ClashCoreKind.Mihomo, coordinator.SessionSnapshot.CoreKind);
            Assert.AreEqual(BackendConnectionState.Online, coordinator.SessionSnapshot.State);
            Assert.IsNull(coordinator.LastErrorMessage);
        }

        [TestMethod]
        public async Task PersistentTransientFailureUsesBoundedExponentialBackoff()
        {
            TrackingTimeProvider timeProvider = new();
            int calls = 0;
            await using AppSessionCoordinator coordinator = CreateCoordinator(session =>
            {
                session.Rest.GetProxiesHandler = _ =>
                {
                    Interlocked.Increment(ref calls);
                    return Task.FromException<ProxyCatalog>(new ClashApiException("Controller unavailable."));
                };
            }, timeProvider);
            await coordinator.InitializeAsync();
            await coordinator.InitialResourceLoadForRecoveryTest.WaitAsync(TestTimeout);

            int[] delays = [2, 4, 8, 16, 30, 30];
            Dictionary<int, int> expectedTimerCounts = [];
            for (int index = 0; index < delays.Length; index++)
            {
                int seconds = delays[index];
                int count = expectedTimerCounts.GetValueOrDefault(seconds) + 1;
                expectedTimerCounts[seconds] = count;
                TimeSpan delay = TimeSpan.FromSeconds(seconds);
                await WaitUntilAsync(() => timeProvider.TimerCount(delay) >= count);

                timeProvider.Advance(delay - TimeSpan.FromMilliseconds(1));
                Assert.AreEqual(index + 1, calls, "The retry must wait for the entire backoff interval.");
                timeProvider.Advance(TimeSpan.FromMilliseconds(1));
                await WaitUntilAsync(() => Volatile.Read(ref calls) == index + 2);
            }

            Assert.AreEqual(BackendConnectionState.Degraded, coordinator.SessionSnapshot.State);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task ConnectedStreamsDoNotRetryPermanentResourceFailures(bool protocolFailure)
        {
            TrackingTimeProvider timeProvider = new();
            int calls = 0;
            await using AppSessionCoordinator coordinator = CreateCoordinator(session =>
            {
                session.Rest.GetProxiesHandler = _ =>
                {
                    Interlocked.Increment(ref calls);
                    Exception exception = protocolFailure
                        ? new ClashProtocolException("The response has an incompatible schema.", null)
                        : new ClashApiException("The endpoint does not exist.", HttpStatusCode.NotFound);
                    return Task.FromException<ProxyCatalog>(exception);
                };
            }, timeProvider);
            await coordinator.InitializeAsync();
            await coordinator.InitialResourceLoadForRecoveryTest.WaitAsync(TestTimeout);
            FakeBackendSession session = (FakeBackendSession)coordinator.ActiveSession!;

            for (int iteration = 1; iteration <= 3; iteration++)
            {
                await WaitUntilAsync(() => timeProvider.TimerCount(TimeSpan.FromSeconds(2)) >= iteration);
                PublishConnectedStreams(session, timeProvider.GetUtcNow());
                timeProvider.Advance(TimeSpan.FromSeconds(2));
                await WaitUntilAsync(() => timeProvider.TimerCount(TimeSpan.FromSeconds(2)) > iteration);
            }

            Assert.AreEqual(1, calls);
            Assert.AreEqual(BackendConnectionState.Degraded, coordinator.SessionSnapshot.State);
            Assert.IsNull(coordinator.ProxyCatalog);
        }

        [TestMethod]
        public async Task UnauthorizedResourceFailureRemainsTerminalAfterStreamsRecover()
        {
            TrackingTimeProvider timeProvider = new();
            int calls = 0;
            await using AppSessionCoordinator coordinator = CreateCoordinator(session =>
            {
                session.Rest.GetProxiesHandler = _ =>
                {
                    Interlocked.Increment(ref calls);
                    return Task.FromException<ProxyCatalog>(new ClashAuthenticationException(null));
                };
            }, timeProvider);
            await coordinator.InitializeAsync();
            await coordinator.InitialResourceLoadForRecoveryTest.WaitAsync(TestTimeout);
            FakeBackendSession session = (FakeBackendSession)coordinator.ActiveSession!;

            PublishConnectedStreams(session, timeProvider.GetUtcNow());
            timeProvider.Advance(TimeSpan.FromMinutes(1));
            await coordinator.ResourceRecoveryForRecoveryTest.WaitAsync(TestTimeout);

            Assert.AreEqual(1, calls);
            Assert.AreEqual(BackendConnectionState.Unauthorized, coordinator.SessionSnapshot.State);
        }

        [TestMethod]
        public async Task SwitchingEpochCancelsInFlightRecoveryBeforeNewSessionPublishes()
        {
            TrackingTimeProvider timeProvider = new();
            BackendProfile profileA = CreateProfile("Backend A", 9090);
            BackendProfile profileB = CreateProfile("Backend B", 9091);
            TaskCompletionSource retryEntered = NewCompletion();
            TaskCompletionSource retryCanceled = NewCompletion();
            int oldCalls = 0;
            FakeBackendSessionFactory sessions = new()
            {
                CreateHandler = (profile, _, epoch, _) =>
                {
                    FakeBackendSession session = new(epoch, profile);
                    session.Rest.GetProxiesHandler = async token =>
                    {
                        if (profile.Id == profileB.Id)
                        {
                            return Catalog("Backend B");
                        }

                        if (Interlocked.Increment(ref oldCalls) == 1)
                        {
                            throw new ClashApiException("Backend A is unavailable.");
                        }

                        retryEntered.TrySetResult();
                        try
                        {
                            await Task.Delay(Timeout.InfiniteTimeSpan, token);
                        }
                        finally
                        {
                            retryCanceled.TrySetResult();
                        }

                        return Catalog("Stale backend A");
                    };
                    return ValueTask.FromResult<IBackendSession>(session);
                },
            };
            await using AppSessionCoordinator coordinator = new(
                new FakeBackendProfileStore
                {
                    Current = new BackendProfileSet
                    {
                        Profiles = [profileA, profileB],
                        ActiveProfileId = profileA.Id,
                    },
                },
                new FakeBackendCredentialStore(),
                sessions,
                new InlineTestDispatcher(),
                new AppSettingsState(),
                timeProvider);
            await coordinator.InitializeAsync();
            await coordinator.InitialResourceLoadForRecoveryTest.WaitAsync(TestTimeout);
            await WaitUntilAsync(() => timeProvider.TimerCount(TimeSpan.FromSeconds(2)) == 1);
            timeProvider.Advance(TimeSpan.FromSeconds(2));
            await retryEntered.Task.WaitAsync(TestTimeout);

            await coordinator.ActivateProfileAsync(profileB.Id).WaitAsync(TestTimeout);
            await retryCanceled.Task.WaitAsync(TestTimeout);
            await coordinator.InitialResourceLoadForRecoveryTest.WaitAsync(TestTimeout);

            Assert.AreEqual(2, oldCalls);
            Assert.AreEqual(profileB.Id, coordinator.ActiveProfile?.Id);
            Assert.AreEqual(profileB.Id, coordinator.SessionSnapshot.ProfileId);
            Assert.AreEqual("Backend B", coordinator.ProxyCatalog?.Proxies.Keys.Single());
            Assert.AreEqual(BackendConnectionState.Online, coordinator.SessionSnapshot.State);
            Assert.IsNull(coordinator.LastErrorMessage);
        }

        [TestMethod]
        public async Task CancelUserOperationPreservesSessionAndAllowsNextOperation()
        {
            await using AppSessionCoordinator coordinator = CreateCoordinator();
            await coordinator.InitializeAsync();
            await coordinator.InitialResourceLoadForRecoveryTest.WaitAsync(TestTimeout);
            IBackendSession? session = coordinator.ActiveSession;
            TaskCompletionSource entered = NewCompletion();
            Task operation = coordinator.RunUserOperationAsync(async token =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });
            await entered.Task.WaitAsync(TestTimeout);
            Assert.IsTrue(coordinator.CanCancelUserOperation);

            coordinator.CancelUserOperation();
            _ = await Assert.ThrowsAsync<OperationCanceledException>(() => operation);

            Assert.IsFalse(coordinator.IsUserOperationRunning);
            Assert.IsFalse(coordinator.CanCancelUserOperation);
            Assert.IsNull(coordinator.LastUserOperationErrorMessage);
            Assert.AreSame(session, coordinator.ActiveSession);
            Assert.IsFalse(session!.Lifetime.IsCancellationRequested);
            int nextOperationCount = 0;
            await coordinator.RunUserOperationAsync(_ =>
            {
                Interlocked.Increment(ref nextOperationCount);
                return Task.CompletedTask;
            });
            Assert.AreEqual(1, nextOperationCount);
        }

        [TestMethod]
        [DataRow("success")]
        [DataRow("failure")]
        [DataRow("unauthorized")]
        public async Task CanceledRefreshDiscardsLateSuccessAndFailure(string lateResult)
        {
            await using AppSessionCoordinator coordinator = CreateCoordinator(session =>
                session.Rest.ProxiesResult = Catalog("Before cancellation"));
            await coordinator.InitializeAsync();
            await coordinator.InitialResourceLoadForRecoveryTest.WaitAsync(TestTimeout);
            FakeBackendSession session = (FakeBackendSession)coordinator.ActiveSession!;
            TaskCompletionSource entered = NewCompletion();
            TaskCompletionSource<ProxyCatalog> response = NewCompletion<ProxyCatalog>();
            CancellationToken requestToken = default;
            session.Rest.GetProxiesHandler = token =>
            {
                requestToken = token;
                entered.TrySetResult();
                return response.Task;
            };

            Task refresh = coordinator.RunUserOperationAsync(coordinator.RefreshAsync);
            await entered.Task.WaitAsync(TestTimeout);
            coordinator.CancelUserOperation();
            await WaitUntilAsync(() => requestToken.IsCancellationRequested);
            if (lateResult == "failure")
            {
                response.SetException(new ClashApiException("The canceled request failed late."));
            }
            else if (lateResult == "unauthorized")
            {
                response.SetException(new ClashAuthenticationException(null));
            }
            else
            {
                response.SetResult(Catalog("Canceled response"));
            }

            await CompleteCanceledOperationAsync(refresh);

            Assert.AreEqual("Before cancellation", coordinator.ProxyCatalog?.Proxies.Keys.Single());
            Assert.AreEqual(BackendConnectionState.Online, coordinator.SessionSnapshot.State);
            Assert.IsNull(coordinator.LastErrorMessage);
            Assert.IsNull(coordinator.LastUserOperationErrorMessage);
            Assert.AreSame(session, coordinator.ActiveSession);
            Assert.IsFalse(coordinator.IsRefreshing);
            Assert.IsFalse(coordinator.IsUserOperationRunning);

            session.Rest.GetProxiesHandler = _ => Task.FromResult(Catalog("After cancellation"));
            await coordinator.RunUserOperationAsync(coordinator.RefreshAsync);
            Assert.AreEqual("After cancellation", coordinator.ProxyCatalog?.Proxies.Keys.Single());
        }

        [TestMethod]
        public async Task ProtectedUserOperationIgnoresCancelRequest()
        {
            await using AppSessionCoordinator coordinator = CreateCoordinator();
            TaskCompletionSource entered = NewCompletion();
            TaskCompletionSource release = NewCompletion();
            CancellationToken operationToken = default;
            Task operation = coordinator.RunUserOperationAsync(async token =>
            {
                operationToken = token;
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }, allowCancellation: false);
            await entered.Task.WaitAsync(TestTimeout);

            Assert.IsTrue(coordinator.IsUserOperationRunning);
            Assert.IsFalse(coordinator.CanCancelUserOperation);
            coordinator.CancelUserOperation();
            Assert.IsFalse(operationToken.IsCancellationRequested);
            Assert.IsFalse(operation.IsCompleted);

            release.SetResult();
            await operation.WaitAsync(TestTimeout);
            Assert.IsFalse(coordinator.IsUserOperationRunning);
            Assert.IsNull(coordinator.LastUserOperationErrorMessage);
        }

        private static AppSessionCoordinator CreateCoordinator(
            Action<FakeBackendSession>? configure = null,
            TimeProvider? timeProvider = null,
            bool recoveringVersion = false)
        {
            BackendProfile profile = CreateProfile("Backend", 9090);
            FakeBackendSessionFactory sessions = new()
            {
                CreateHandler = (createdProfile, _, epoch, _) =>
                {
                    FakeBackendSession session = new(epoch, createdProfile,
                        snapshot: recoveringVersion ? new BackendSessionSnapshot
                        {
                            Epoch = epoch,
                            ProfileId = createdProfile.Id,
                            State = BackendConnectionState.OfflineRetrying,
                            StatusDetail = "The initial backend connection timed out.",
                        } : null);
                    configure?.Invoke(session);
                    return ValueTask.FromResult<IBackendSession>(session);
                },
            };
            return new AppSessionCoordinator(
                new FakeBackendProfileStore
                {
                    Current = new BackendProfileSet { Profiles = [profile], ActiveProfileId = profile.Id },
                },
                new FakeBackendCredentialStore(),
                sessions,
                new InlineTestDispatcher(),
                new AppSettingsState(),
                timeProvider ?? TimeProvider.System);
        }

        private static void SetResourceHandler(
            FakeClashRestClient rest,
            string resource,
            Func<Exception?> failure)
        {
            switch (resource)
            {
                case "configuration":
                    rest.GetConfigurationHandler = _ => ResultOrFailure(new ClashConfiguration(), failure());
                    break;
                case "proxies":
                    rest.GetProxiesHandler = _ => ResultOrFailure(Catalog("Recovered"), failure());
                    break;
                case "proxy providers":
                    rest.GetProxyProvidersHandler = _ => ResultOrFailure(new ProxyProviderCatalog(), failure());
                    break;
                case "rules":
                    rest.GetRulesHandler = _ => ResultOrFailure(new RuleCatalog(), failure());
                    break;
                case "rule providers":
                    rest.GetRuleProvidersHandler = _ => ResultOrFailure(new RuleProviderCatalog(), failure());
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(resource));
            }
        }

        private static Task<T> ResultOrFailure<T>(T value, Exception? exception) =>
            exception is null ? Task.FromResult(value) : Task.FromException<T>(exception);

        private static void PublishConnectedStreams(FakeBackendSession session, DateTimeOffset now)
        {
            foreach (ClashStreamKind kind in Enum.GetValues<ClashStreamKind>())
            {
                session.Streams.RaiseStatus(new ClashStreamStatus(kind, ClashStreamState.Connected, 0, now));
            }
        }

        private static BackendProfile CreateProfile(string name, int port) => new(
            Guid.NewGuid(), name, BackendEndpoint.Create($"http://127.0.0.1:{port}"));

        private static ProxyCatalog Catalog(string name) => new()
        {
            Proxies = new Dictionary<string, ClashProxy>(StringComparer.Ordinal)
            {
                [name] = new ClashProxy { Name = name, Type = "Direct", Kind = ClashProxyKind.Direct },
            },
        };

        private static TaskCompletionSource NewCompletion() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static TaskCompletionSource<T> NewCompletion<T>() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            using CancellationTokenSource timeout = new(TestTimeout);
            while (!condition())
            {
                await Task.Delay(10, timeout.Token);
            }
        }

        private static async Task CompleteCanceledOperationAsync(Task operation)
        {
            try
            {
                await operation.WaitAsync(TestTimeout);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private sealed class TrackingTimeProvider : TimeProvider
        {
            private readonly ManualTimeProvider _inner = new();
            private readonly Dictionary<TimeSpan, int> _timerCounts = [];
            private readonly Lock _gate = new();

            public override TimeZoneInfo LocalTimeZone => _inner.LocalTimeZone;

            public override long TimestampFrequency => _inner.TimestampFrequency;

            public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();

            public override long GetTimestamp() => _inner.GetTimestamp();

            public override ITimer CreateTimer(
                TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            {
                ITimer timer = _inner.CreateTimer(callback, state, dueTime, period);
                lock (_gate)
                {
                    _timerCounts[dueTime] = _timerCounts.GetValueOrDefault(dueTime) + 1;
                }

                return timer;
            }

            public int TimerCount(TimeSpan delay)
            {
                lock (_gate)
                {
                    return _timerCounts.GetValueOrDefault(delay);
                }
            }

            public void Advance(TimeSpan amount) => _inner.Advance(amount);
        }
    }
}

namespace Zashboard.App.Services
{
    public sealed partial class AppSessionCoordinator
    {
        internal Task InitialResourceLoadForRecoveryTest => _backgroundTasks[0];

        internal Task ResourceRecoveryForRecoveryTest => _backgroundTasks[^1];
    }
}
