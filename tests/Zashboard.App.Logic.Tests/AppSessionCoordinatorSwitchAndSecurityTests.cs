using Zashboard.App.Services;
using Zashboard.Core.Backends;
using Zashboard.Core.Capabilities;
using Zashboard.Core.Clash;
using Zashboard.Core.Sessions;
using Zashboard.Infrastructure.Clash;
using Zashboard.Infrastructure.Persistence;

namespace Zashboard.App.Logic.Tests;

[TestClass]
public sealed class AppSessionCoordinatorSwitchAndSecurityTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task SuccessfulProfileSwitchRejectsQueuedAndLateCallbacksFromOldEpoch()
    {
        BackendProfile profileA = CreateProfile("Backend A", 9090);
        BackendProfile profileB = CreateProfile("Backend B", 9091);
        FakeBackendProfileStore profiles = CreateProfileStore(
            [profileA, profileB],
            profileA.Id);
        FakeBackendCredentialStore credentials = new();
        FakeBackendSessionFactory sessions = new();
        QueuedTestDispatcher dispatcher = new();
        AppSessionCoordinator coordinator = CreateCoordinator(
            profiles,
            credentials,
            sessions,
            dispatcher);

        await coordinator.RunUserOperationAsync(coordinator.InitializeAsync);
        FakeBackendSession sessionA = (FakeBackendSession)sessions.CreatedSessions.Single();
        await sessionA.Streams.AllStarted.WaitAsync(TestTimeout);
        await coordinator.RunUserOperationAsync(coordinator.RefreshAsync);

        dispatcher.HoldNextInvocation();
        sessionA.Streams.RaiseStatus(StreamStatus(
            ClashStreamKind.Connections,
            ClashStreamState.Unauthorized));
        await dispatcher.NextInvocationQueued.WaitAsync(TestTimeout);

        await coordinator.RunUserOperationAsync(
            token => coordinator.ActivateProfileAsync(profileB.Id, token));
        FakeBackendSession sessionB = (FakeBackendSession)sessions.CreatedSessions.Last();
        await sessionB.Streams.AllStarted.WaitAsync(TestTimeout);
        await coordinator.RunUserOperationAsync(coordinator.RefreshAsync);

        sessionA.Streams.RaiseStatus(StreamStatus(
            ClashStreamKind.Traffic,
            ClashStreamState.Unauthorized));
        sessionA.Capabilities.Observe(
            ClashCapability.CoreRestart,
            CapabilitySupport.Unsupported,
            CapabilityEvidenceKind.MethodNotAllowed,
            DateTimeOffset.UtcNow);
        dispatcher.ReleaseNext();

        Assert.AreEqual(1, sessionA.DisposeCount);
        Assert.AreSame(sessionB, coordinator.ActiveSession);
        Assert.AreEqual(profileB.Id, coordinator.ActiveProfile?.Id);
        Assert.AreEqual(profileB.Id, coordinator.SessionSnapshot.ProfileId);
        Assert.AreEqual(BackendConnectionState.Online, coordinator.SessionSnapshot.State);
        Assert.AreEqual(
            CapabilitySupport.Unknown,
            coordinator.SessionSnapshot.Capabilities[ClashCapability.CoreRestart].Support);

        await coordinator.DisposeAsync();
    }

    private static void AssertConnectedStatusesDoNotChangeUnauthorized(
        AppSessionCoordinator coordinator,
        FakeBackendSession session)
    {
        foreach (ClashStreamKind kind in Enum.GetValues<ClashStreamKind>())
        {
            session.Streams.RaiseStatus(StreamStatus(kind, ClashStreamState.Connected));
        }

        Assert.AreEqual(BackendConnectionState.Unauthorized, coordinator.SessionSnapshot.State);
    }

    private static ClashStreamStatus StreamStatus(
        ClashStreamKind kind,
        ClashStreamState state) => new(
        kind,
        state,
        0,
        DateTimeOffset.UtcNow,
        state == ClashStreamState.Unauthorized ? 401 : null);

    private static AppSessionCoordinator CreateCoordinator(
        FakeBackendProfileStore profiles,
        FakeBackendCredentialStore credentials,
        IBackendSessionFactory sessions,
        IUiDispatcher dispatcher) => new(
        profiles,
        credentials,
        sessions,
        dispatcher,
        new AppSettingsState(),
        TimeProvider.System);

    private static FakeBackendProfileStore CreateProfileStore(
        IReadOnlyList<BackendProfile> profiles,
        Guid? activeProfileId) => new()
    {
        Current = new BackendProfileSet
        {
            Profiles = profiles,
            ActiveProfileId = activeProfileId,
        },
    };

    private static BackendProfile CreateProfile(string name, int port) => new(
        Guid.NewGuid(),
        name,
        BackendEndpoint.Create($"http://127.0.0.1:{port}"));

    private static ProxyCatalog SmartProxyCatalog() => new()
    {
        Proxies = new Dictionary<string, ClashProxy>(StringComparer.Ordinal)
        {
            ["Smart"] = new ClashProxy
            {
                Name = "Smart",
                Type = "Smart",
                Kind = ClashProxyKind.Smart,
                All = ["Node A"],
            },
        },
    };

    [TestMethod]
    public async Task StreamUnauthorizedCannotBeOverwrittenByOtherConnectedStreams()
    {
        BackendProfile profile = CreateProfile("Backend", 9090);
        FakeBackendSessionFactory sessions = new();
        AppSessionCoordinator coordinator = CreateCoordinator(
            CreateProfileStore([profile], profile.Id),
            new FakeBackendCredentialStore(),
            sessions,
            new InlineTestDispatcher());
        await coordinator.RunUserOperationAsync(coordinator.InitializeAsync);
        FakeBackendSession session = (FakeBackendSession)sessions.CreatedSessions.Single();
        await session.Streams.AllStarted.WaitAsync(TestTimeout);

        session.Streams.RaiseStatus(StreamStatus(
            ClashStreamKind.Connections,
            ClashStreamState.Unauthorized));
        AssertConnectedStatusesDoNotChangeUnauthorized(coordinator, session);

        Assert.IsNotNull(coordinator.SessionSnapshot.StatusDetail);
        StringAssert.Contains(
            coordinator.SessionSnapshot.StatusDetail,
            "connections stream");

        await coordinator.DisposeAsync();
    }

    [TestMethod]
    public async Task OnlineRestAuthenticationFailurePublishesUnauthorizedState()
    {
        BackendProfile profile = CreateProfile("Backend", 9090);
        FakeBackendProfileStore profiles = CreateProfileStore([profile], profile.Id);
        FakeBackendSessionFactory sessions = new();
        AppSessionCoordinator coordinator = CreateCoordinator(
            profiles,
            new FakeBackendCredentialStore(),
            sessions,
            new InlineTestDispatcher());
        await coordinator.RunUserOperationAsync(coordinator.InitializeAsync);
        FakeBackendSession session = (FakeBackendSession)sessions.CreatedSessions.Single();
        await session.Streams.AllStarted.WaitAsync(TestTimeout);
        session.Rest.FlushDnsCacheHandler = _ => Task.FromException(
            new ClashAuthenticationException(requestUri: null));

        ClashAuthenticationException exception =
            await Assert.ThrowsExactlyAsync<ClashAuthenticationException>(() =>
                coordinator.RunUserOperationAsync(coordinator.FlushDnsCacheAsync));

        Assert.AreEqual(BackendConnectionState.Unauthorized, coordinator.SessionSnapshot.State);
        Assert.AreEqual(exception.Message, coordinator.LastUserOperationErrorMessage);
        Assert.IsNotNull(coordinator.LastErrorMessage);
        StringAssert.Contains(coordinator.LastErrorMessage, "controller request");
        AssertConnectedStatusesDoNotChangeUnauthorized(coordinator, session);

        await coordinator.DisposeAsync();
    }

    [TestMethod]
    public async Task RemovingActiveProfileDeletesCredentialAndStopsSession()
    {
        BackendProfile profile = CreateProfile("Backend", 9090);
        FakeBackendProfileStore profiles = CreateProfileStore([profile], profile.Id);
        FakeBackendCredentialStore credentials = new();
        credentials.Seed(profile.Id, "stored-secret");
        FakeBackendSessionFactory sessions = new();
        AppSessionCoordinator coordinator = CreateCoordinator(
            profiles,
            credentials,
            sessions,
            new InlineTestDispatcher());
        await coordinator.RunUserOperationAsync(coordinator.InitializeAsync);
        FakeBackendSession session = (FakeBackendSession)sessions.CreatedSessions.Single();
        await session.Streams.AllStarted.WaitAsync(TestTimeout);
        await coordinator.RunUserOperationAsync(coordinator.RefreshAsync);
        Assert.IsNotNull(coordinator.Configuration);

        await coordinator.RunUserOperationAsync(
            token => coordinator.RemoveProfileAsync(profile.Id, token));

        Assert.IsEmpty(profiles.Current.Profiles);
        Assert.IsNull(profiles.Current.ActiveProfileId);
        Assert.IsEmpty(coordinator.Profiles);
        Assert.IsNull(credentials.GetStored(profile.Id));
        CollectionAssert.AreEqual(new[] { profile.Id }, credentials.DeleteCalls.ToArray());
        Assert.AreEqual(1, session.DisposeCount);
        Assert.IsNull(coordinator.ActiveSession);
        Assert.IsNull(coordinator.ActiveProfile);
        Assert.IsNull(coordinator.Configuration);
        Assert.AreEqual(BackendConnectionState.NoBackend, coordinator.SessionSnapshot.State);

        await coordinator.DisposeAsync();
    }

    [TestMethod]
    public async Task CredentialDeleteFailureRollsBackActiveProfileAndKeepsSession()
    {
        BackendProfile profile = CreateProfile("Backend", 9090);
        FakeBackendProfileStore profiles = CreateProfileStore([profile], profile.Id);
        FakeBackendCredentialStore credentials = new()
        {
            DeleteException = new IOException("Credential delete failed."),
        };
        credentials.Seed(profile.Id, "stored-secret");
        FakeBackendSessionFactory sessions = new();
        AppSessionCoordinator coordinator = CreateCoordinator(
            profiles,
            credentials,
            sessions,
            new InlineTestDispatcher());
        await coordinator.RunUserOperationAsync(coordinator.InitializeAsync);
        FakeBackendSession session = (FakeBackendSession)sessions.CreatedSessions.Single();
        await session.Streams.AllStarted.WaitAsync(TestTimeout);

        IOException exception = await Assert.ThrowsExactlyAsync<IOException>(() =>
            coordinator.RunUserOperationAsync(
                token => coordinator.RemoveProfileAsync(profile.Id, token)));

        Assert.AreEqual("Credential delete failed.", exception.Message);
        Assert.HasCount(2, profiles.Saves);
        Assert.AreEqual(profile.Id, profiles.Current.Profiles.Single().Id);
        Assert.AreEqual(profile.Id, profiles.Current.ActiveProfileId);
        Assert.AreEqual(profile.Id, coordinator.Profiles.Single().Id);
        Assert.AreSame(session, coordinator.ActiveSession);
        Assert.AreEqual(0, session.DisposeCount);
        Assert.AreEqual("stored-secret", credentials.GetStored(profile.Id)?.Secret);

        await coordinator.DisposeAsync();
    }

    [TestMethod]
    public async Task ClearingStoredSecretReconnectsWithEmptyCredential()
    {
        BackendProfile profile = CreateProfile("Backend", 9090);
        FakeBackendProfileStore profiles = CreateProfileStore([profile], profile.Id);
        FakeBackendCredentialStore credentials = new();
        credentials.Seed(profile.Id, "stored-secret");
        FakeBackendSessionFactory sessions = new();
        AppSessionCoordinator coordinator = CreateCoordinator(
            profiles,
            credentials,
            sessions,
            new InlineTestDispatcher());
        await coordinator.RunUserOperationAsync(coordinator.InitializeAsync);
        FakeBackendSession initialSession = (FakeBackendSession)sessions.CreatedSessions.Single();
        await initialSession.Streams.AllStarted.WaitAsync(TestTimeout);
        Assert.IsTrue(coordinator.HasStoredCredential(profile.Id));

        Guid savedProfileId = Guid.Empty;
        await coordinator.RunUserOperationAsync(async token =>
        {
            savedProfileId = await coordinator.SaveAndConnectAsync(
                new BackendSaveRequest(
                    profile.Id,
                    profile.Name,
                    new Uri("http://127.0.0.1:9091"),
                    string.Empty,
                    BackendCredentialUpdate.Remove),
                token);
        });
        FakeBackendSession replacement = (FakeBackendSession)sessions.CreatedSessions.Last();
        await replacement.Streams.AllStarted.WaitAsync(TestTimeout);

        Assert.HasCount(2, sessions.Calls);
        Assert.AreEqual("stored-secret", sessions.Calls[0].Credential.Secret);
        Assert.AreEqual(string.Empty, sessions.Calls[1].Credential.Secret);
        Assert.AreNotEqual(profile.Id, savedProfileId);
        Assert.AreEqual(savedProfileId, profiles.Current.Profiles.Single().Id);
        Assert.AreEqual(savedProfileId, profiles.Current.ActiveProfileId);
        Assert.AreEqual(savedProfileId, sessions.Calls[1].Profile.Id);
        Assert.IsNull(credentials.GetStored(profile.Id));
        Assert.IsFalse(coordinator.HasStoredCredential(profile.Id));
        Assert.IsFalse(coordinator.HasStoredCredential(savedProfileId));
        CollectionAssert.AreEqual(new[] { profile.Id }, credentials.DeleteCalls.ToArray());
        Assert.AreEqual(1, initialSession.DisposeCount);
        Assert.AreSame(replacement, coordinator.ActiveSession);

        await coordinator.DisposeAsync();
    }

    [TestMethod]
    public async Task EndpointChangeWithoutCredentialChoiceIsRejectedBeforeSessionSwitch()
    {
        BackendProfile profile = CreateProfile("Backend", 9090);
        FakeBackendProfileStore profiles = CreateProfileStore([profile], profile.Id);
        FakeBackendCredentialStore credentials = new();
        credentials.Seed(profile.Id, "stored-secret");
        FakeBackendSessionFactory sessions = new();
        AppSessionCoordinator coordinator = CreateCoordinator(
            profiles,
            credentials,
            sessions,
            new InlineTestDispatcher());
        await coordinator.RunUserOperationAsync(coordinator.InitializeAsync);
        FakeBackendSession initialSession = (FakeBackendSession)sessions.CreatedSessions.Single();
        await initialSession.Streams.AllStarted.WaitAsync(TestTimeout);

        InvalidOperationException exception =
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                coordinator.RunUserOperationAsync(token =>
                    coordinator.SaveAndConnectAsync(
                        new BackendSaveRequest(
                            profile.Id,
                            profile.Name,
                            new Uri("http://127.0.0.1:9091"),
                            string.Empty,
                            BackendCredentialUpdate.Keep),
                        token)));

        StringAssert.Contains(exception.Message, "address changed");
        Assert.IsEmpty(profiles.Saves);
        Assert.AreEqual(profile.Endpoint, profiles.Current.Profiles.Single().Endpoint);
        Assert.AreEqual("stored-secret", credentials.GetStored(profile.Id)?.Secret);
        Assert.HasCount(1, sessions.Calls);
        Assert.AreEqual(0, initialSession.DisposeCount);
        Assert.AreSame(initialSession, coordinator.ActiveSession);

        await coordinator.DisposeAsync();
    }

    [TestMethod]
    public async Task EndpointChangeWithReplacementUsesOnlyReplacementCredential()
    {
        BackendProfile profile = CreateProfile("Backend", 9090);
        FakeBackendProfileStore profiles = CreateProfileStore([profile], profile.Id);
        FakeBackendCredentialStore credentials = new();
        credentials.Seed(profile.Id, "stored-secret");
        FakeBackendSessionFactory sessions = new();
        AppSessionCoordinator coordinator = CreateCoordinator(
            profiles,
            credentials,
            sessions,
            new InlineTestDispatcher());
        await coordinator.RunUserOperationAsync(coordinator.InitializeAsync);
        FakeBackendSession initialSession = (FakeBackendSession)sessions.CreatedSessions.Single();
        await initialSession.Streams.AllStarted.WaitAsync(TestTimeout);

        Guid savedProfileId = Guid.Empty;
        await coordinator.RunUserOperationAsync(async token =>
        {
            savedProfileId = await coordinator.SaveAndConnectAsync(
                new BackendSaveRequest(
                    profile.Id,
                    profile.Name,
                    new Uri("http://127.0.0.1:9091"),
                    "replacement-secret",
                    BackendCredentialUpdate.Replace),
                token);
        });
        FakeBackendSession replacement = (FakeBackendSession)sessions.CreatedSessions.Last();
        await replacement.Streams.AllStarted.WaitAsync(TestTimeout);

        Assert.HasCount(2, sessions.Calls);
        Assert.AreEqual("stored-secret", sessions.Calls[0].Credential.Secret);
        Assert.AreEqual("replacement-secret", sessions.Calls[1].Credential.Secret);
        Assert.AreNotEqual(profile.Id, savedProfileId);
        Assert.AreEqual(savedProfileId, profiles.Current.Profiles.Single().Id);
        Assert.AreEqual(savedProfileId, profiles.Current.ActiveProfileId);
        Assert.AreEqual(savedProfileId, sessions.Calls[1].Profile.Id);
        Assert.IsNull(credentials.GetStored(profile.Id));
        Assert.AreEqual("replacement-secret", credentials.GetStored(savedProfileId)?.Secret);
        Assert.IsFalse(coordinator.HasStoredCredential(profile.Id));
        Assert.IsTrue(coordinator.HasStoredCredential(savedProfileId));
        Assert.HasCount(1, credentials.SetCalls);
        Assert.AreEqual(savedProfileId, credentials.SetCalls[0].ProfileId);
        CollectionAssert.AreEqual(new[] { profile.Id }, credentials.DeleteCalls.ToArray());
        Assert.AreEqual(1, initialSession.DisposeCount);
        Assert.AreSame(replacement, coordinator.ActiveSession);

        await coordinator.DisposeAsync();
    }

    [TestMethod]
    public async Task EndpointChangeProfileCommitFailureRemovesPreparedCredential()
    {
        BackendProfile profile = CreateProfile("Backend", 9090);
        FakeBackendProfileStore profiles = CreateProfileStore([profile], profile.Id);
        FakeBackendCredentialStore credentials = new();
        credentials.Seed(profile.Id, "stored-secret");
        FakeBackendSessionFactory sessions = new();
        AppSessionCoordinator coordinator = CreateCoordinator(
            profiles,
            credentials,
            sessions,
            new InlineTestDispatcher());
        await coordinator.RunUserOperationAsync(coordinator.InitializeAsync);
        FakeBackendSession initialSession = (FakeBackendSession)sessions.CreatedSessions.Single();
        await initialSession.Streams.AllStarted.WaitAsync(TestTimeout);
        profiles.SaveException = new IOException("Profile write failed.");

        IOException exception = await Assert.ThrowsExactlyAsync<IOException>(() =>
            coordinator.RunUserOperationAsync(token =>
                coordinator.SaveAndConnectAsync(
                    new BackendSaveRequest(
                        profile.Id,
                        profile.Name,
                        new Uri("http://127.0.0.1:9091"),
                        "replacement-secret",
                        BackendCredentialUpdate.Replace),
                    token)));

        Assert.AreEqual("Profile write failed.", exception.Message);
        Assert.HasCount(1, credentials.SetCalls);
        Guid preparedProfileId = credentials.SetCalls[0].ProfileId;
        Assert.AreNotEqual(profile.Id, preparedProfileId);
        Assert.IsNull(credentials.GetStored(preparedProfileId));
        Assert.AreEqual("stored-secret", credentials.GetStored(profile.Id)?.Secret);
        CollectionAssert.AreEqual(new[] { preparedProfileId }, credentials.DeleteCalls.ToArray());
        Assert.AreEqual(profile.Id, profiles.Current.Profiles.Single().Id);
        Assert.AreSame(initialSession, coordinator.ActiveSession);
        Assert.AreEqual(0, initialSession.DisposeCount);

        profiles.SaveException = null;
        await coordinator.DisposeAsync();
    }

    [TestMethod]
    public async Task EndpointChangeOldCredentialCleanupFailureKeepsNewProfileAndWarns()
    {
        BackendProfile profile = CreateProfile("Backend", 9090);
        FakeBackendProfileStore profiles = CreateProfileStore([profile], profile.Id);
        FakeBackendCredentialStore credentials = new();
        credentials.Seed(profile.Id, "stored-secret");
        FakeBackendSessionFactory sessions = new();
        AppSessionCoordinator coordinator = CreateCoordinator(
            profiles,
            credentials,
            sessions,
            new InlineTestDispatcher());
        await coordinator.RunUserOperationAsync(coordinator.InitializeAsync);
        FakeBackendSession initialSession = (FakeBackendSession)sessions.CreatedSessions.Single();
        await initialSession.Streams.AllStarted.WaitAsync(TestTimeout);
        credentials.DeleteException = new IOException("Old credential cleanup failed.");
        Guid savedProfileId = Guid.Empty;

        await coordinator.RunUserOperationAsync(async token =>
        {
            savedProfileId = await coordinator.SaveAndConnectAsync(
                new BackendSaveRequest(
                    profile.Id,
                    profile.Name,
                    new Uri("http://127.0.0.1:9091"),
                    "replacement-secret",
                    BackendCredentialUpdate.Replace),
                token);
        });
        FakeBackendSession replacement = (FakeBackendSession)sessions.CreatedSessions.Last();
        await replacement.Streams.AllStarted.WaitAsync(TestTimeout);

        Assert.AreNotEqual(profile.Id, savedProfileId);
        BackendProfile savedProfile = profiles.Current.Profiles.Single();
        Assert.AreEqual(savedProfileId, savedProfile.Id);
        Assert.AreEqual("http://127.0.0.1:9091/", savedProfile.Endpoint.BaseUri.AbsoluteUri);
        Assert.AreEqual(savedProfileId, profiles.Current.ActiveProfileId);
        Assert.AreEqual(savedProfileId, coordinator.ActiveProfile?.Id);
        Assert.AreEqual(savedProfileId, coordinator.SessionSnapshot.ProfileId);
        Assert.AreEqual("stored-secret", credentials.GetStored(profile.Id)?.Secret);
        Assert.AreEqual("replacement-secret", credentials.GetStored(savedProfileId)?.Secret);
        Assert.IsTrue(coordinator.HasStoredCredential(savedProfileId));
        Assert.HasCount(2, sessions.Calls);
        Assert.AreEqual(savedProfileId, sessions.Calls[1].Profile.Id);
        Assert.AreEqual("replacement-secret", sessions.Calls[1].Credential.Secret);
        Assert.AreSame(replacement, coordinator.ActiveSession);
        Assert.AreEqual(1, initialSession.DisposeCount);
        CollectionAssert.AreEqual(new[] { profile.Id }, credentials.DeleteCalls.ToArray());
        Assert.IsNotNull(coordinator.LastErrorMessage);
        StringAssert.Contains(
            coordinator.LastErrorMessage,
            "previous encrypted credential could not be removed");
        StringAssert.Contains(coordinator.LastErrorMessage, "Old credential cleanup failed.");

        await coordinator.DisposeAsync();
    }

    [TestMethod]
    public async Task UnknownExplicitProfileIdIsRejectedWithoutUsingOrphanCredential()
    {
        BackendProfile profile = CreateProfile("Backend", 9090);
        Guid unknownProfileId = Guid.NewGuid();
        FakeBackendProfileStore profiles = CreateProfileStore([profile], profile.Id);
        FakeBackendCredentialStore credentials = new();
        credentials.Seed(unknownProfileId, "orphan-secret");
        FakeBackendSessionFactory sessions = new();
        AppSessionCoordinator coordinator = CreateCoordinator(
            profiles,
            credentials,
            sessions,
            new InlineTestDispatcher());
        await coordinator.RunUserOperationAsync(coordinator.InitializeAsync);
        FakeBackendSession initialSession = (FakeBackendSession)sessions.CreatedSessions.Single();
        await initialSession.Streams.AllStarted.WaitAsync(TestTimeout);

        _ = await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() =>
            coordinator.RunUserOperationAsync(token =>
                coordinator.SaveAndConnectAsync(
                    new BackendSaveRequest(
                        unknownProfileId,
                        "Unknown",
                        new Uri("http://127.0.0.1:9091"),
                        string.Empty,
                        BackendCredentialUpdate.Remove),
                    token)));

        Assert.IsEmpty(profiles.Saves);
        Assert.AreEqual("orphan-secret", credentials.GetStored(unknownProfileId)?.Secret);
        Assert.IsEmpty(credentials.DeleteCalls);
        Assert.HasCount(1, sessions.Calls);
        Assert.AreSame(initialSession, coordinator.ActiveSession);

        await coordinator.DisposeAsync();
    }

    [TestMethod]
    public async Task ActivatingCurrentProfileIsNoOp()
    {
        BackendProfile profile = CreateProfile("Backend", 9090);
        FakeBackendProfileStore profiles = CreateProfileStore([profile], profile.Id);
        FakeBackendSessionFactory sessions = new();
        AppSessionCoordinator coordinator = CreateCoordinator(
            profiles,
            new FakeBackendCredentialStore(),
            sessions,
            new InlineTestDispatcher());
        await coordinator.RunUserOperationAsync(coordinator.InitializeAsync);
        FakeBackendSession session = (FakeBackendSession)sessions.CreatedSessions.Single();
        await session.Streams.AllStarted.WaitAsync(TestTimeout);

        await coordinator.RunUserOperationAsync(
            token => coordinator.ActivateProfileAsync(profile.Id, token));

        Assert.IsEmpty(profiles.Saves);
        Assert.HasCount(1, sessions.Calls);
        Assert.AreEqual(0, session.DisposeCount);
        Assert.AreSame(session, coordinator.ActiveSession);

        await coordinator.DisposeAsync();
    }

    [TestMethod]
    public async Task SessionCleanupFailureDoesNotLeaveSavedProfileStateStale()
    {
        BackendProfile profile = CreateProfile("Backend", 9090);
        FakeBackendProfileStore profiles = CreateProfileStore([profile], profile.Id);
        int createCount = 0;
        FakeBackendSessionFactory sessions = new()
        {
            CreateHandler = (createdProfile, _, epoch, _) =>
            {
                bool failOnDispose = Interlocked.Increment(ref createCount) == 1;
                return ValueTask.FromResult<IBackendSession>(new FakeBackendSession(
                    epoch,
                    createdProfile,
                    disposeException: failOnDispose
                        ? new InvalidOperationException("Session disposal failed.")
                        : null));
            },
        };
        AppSessionCoordinator coordinator = CreateCoordinator(
            profiles,
            new FakeBackendCredentialStore(),
            sessions,
            new InlineTestDispatcher());
        await coordinator.RunUserOperationAsync(coordinator.InitializeAsync);
        FakeBackendSession initialSession = (FakeBackendSession)sessions.CreatedSessions.Single();
        await initialSession.Streams.AllStarted.WaitAsync(TestTimeout);
        Guid savedProfileId = Guid.Empty;

        await coordinator.RunUserOperationAsync(async token =>
        {
            savedProfileId = await coordinator.SaveAndConnectAsync(
                new BackendSaveRequest(
                    profile.Id,
                    profile.Name,
                    new Uri("http://127.0.0.1:9091"),
                    string.Empty,
                    BackendCredentialUpdate.Remove),
                token);
        });
        FakeBackendSession replacement = (FakeBackendSession)sessions.CreatedSessions.Last();
        await replacement.Streams.AllStarted.WaitAsync(TestTimeout);

        Assert.AreNotEqual(profile.Id, savedProfileId);
        Assert.AreEqual(savedProfileId, profiles.Current.ActiveProfileId);
        Assert.AreEqual(savedProfileId, coordinator.ActiveProfile?.Id);
        Assert.AreEqual(savedProfileId, coordinator.SessionSnapshot.ProfileId);
        Assert.AreSame(replacement, coordinator.ActiveSession);
        Assert.AreEqual(1, initialSession.DisposeCount);
        Assert.IsNotNull(coordinator.LastErrorMessage);
        StringAssert.Contains(coordinator.LastErrorMessage, "could not be cleaned up");

        await coordinator.DisposeAsync();
    }

    [TestMethod]
    public async Task UnsupportedOptionalCapabilitiesClearCachedDataAndUpdateSnapshot()
    {
        BackendProfile profile = CreateProfile("Backend", 9090);
        FakeBackendSessionFactory sessions = new()
        {
            CreateHandler = (createdProfile, _, epoch, _) =>
            {
                FakeBackendSession session = new(epoch, createdProfile);
                session.Rest.ProxiesResult = SmartProxyCatalog();
                session.Rest.SmartWeightsResult = new SmartWeights
                {
                    Message = "cached",
                    Weights = new Dictionary<string, IReadOnlyList<SmartNodeRank>>
                    {
                        ["Smart"] = [new SmartNodeRank
                        {
                            Name = "Node A",
                            Rank = "1",
                            Weight = 1,
                        }],
                    },
                };
                session.Rest.HonkStatisticsResult = new HonkRuntimeStatistics
                {
                    Outbounds = [new HonkOutboundStatistics { Name = "Node A" }],
                };
                return ValueTask.FromResult<IBackendSession>(session);
            },
        };
        AppSessionCoordinator coordinator = CreateCoordinator(
            CreateProfileStore([profile], profile.Id),
            new FakeBackendCredentialStore(),
            sessions,
            new InlineTestDispatcher());
        await coordinator.RunUserOperationAsync(coordinator.InitializeAsync);
        FakeBackendSession session = (FakeBackendSession)sessions.CreatedSessions.Single();
        await session.Streams.AllStarted.WaitAsync(TestTimeout);
        session.Capabilities.Observe(
            ClashCapability.SmartWeights,
            CapabilitySupport.Supported,
            CapabilityEvidenceKind.SuccessfulCall,
            DateTimeOffset.UtcNow);
        session.Capabilities.Observe(
            ClashCapability.RuntimeStatistics,
            CapabilitySupport.Supported,
            CapabilityEvidenceKind.SuccessfulCall,
            DateTimeOffset.UtcNow);
        await coordinator.RunUserOperationAsync(coordinator.RefreshAsync);

        Assert.IsNotNull(coordinator.SmartWeights);
        Assert.IsNotNull(coordinator.HonkRuntimeStatistics);

        session.Capabilities.Observe(
            ClashCapability.SmartWeights,
            CapabilitySupport.Unsupported,
            CapabilityEvidenceKind.MethodNotAllowed,
            DateTimeOffset.UtcNow);
        session.Capabilities.Observe(
            ClashCapability.RuntimeStatistics,
            CapabilitySupport.Unsupported,
            CapabilityEvidenceKind.MethodNotAllowed,
            DateTimeOffset.UtcNow);

        Assert.IsNull(coordinator.SmartWeights);
        Assert.IsNull(coordinator.HonkRuntimeStatistics);
        Assert.AreEqual(
            CapabilitySupport.Unsupported,
            coordinator.SessionSnapshot.Capabilities[ClashCapability.SmartWeights].Support);
        Assert.AreEqual(
            CapabilitySupport.Unsupported,
            coordinator.SessionSnapshot.Capabilities[ClashCapability.RuntimeStatistics].Support);

        await coordinator.DisposeAsync();
    }
}
