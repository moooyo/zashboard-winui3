using Zashboard.App.Services;
using Zashboard.Core.Backends;
using Zashboard.Infrastructure.Persistence;

namespace Zashboard.App.Logic.Tests;

[TestClass]
public sealed class AppSessionCoordinatorProfileSafetyTests
{
    [TestMethod]
    public async Task FailedProfileLoadBlocksEveryProfileWriteAndCredentialChange()
    {
        BackendProfile existing = CreateProfile("Existing", 9090);
        BackendProfileSet original = new()
        {
            Profiles = [existing],
            ActiveProfileId = existing.Id,
        };
        FakeBackendProfileStore profiles = new()
        {
            Current = original,
            LoadException = new IOException("The profile file is temporarily unavailable."),
        };
        FakeBackendCredentialStore credentials = new();
        credentials.Seed(existing.Id, "existing-secret");
        FakeBackendSessionFactory sessions = new();
        await using AppSessionCoordinator coordinator = CreateCoordinator(profiles, credentials, sessions);

        await coordinator.RunUserOperationAsync(coordinator.InitializeAsync);

        Assert.AreEqual(BackendProfilesLoadState.Failed, coordinator.ProfileLoadState);
        Assert.IsFalse(coordinator.IsInitialized);
        Assert.IsFalse(coordinator.CanWriteProfiles);
        Assert.IsNotNull(coordinator.ProfileLoadError);
        StringAssert.Contains(coordinator.ProfileLoadError, "temporarily unavailable");

        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            coordinator.RunUserOperationAsync(token => coordinator.SaveAndConnectAsync(
                NewProfileRequest(), token)));
        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            coordinator.RunUserOperationAsync(token => coordinator.ActivateProfileAsync(
                existing.Id, token)));
        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            coordinator.RunUserOperationAsync(token => coordinator.RemoveProfileAsync(
                existing.Id, token)));

        Assert.AreSame(original, profiles.Current);
        Assert.IsEmpty(profiles.Saves);
        Assert.IsEmpty(credentials.SetCalls);
        Assert.IsEmpty(credentials.DeleteCalls);
        Assert.AreEqual("existing-secret", credentials.GetStored(existing.Id)?.Secret);
        Assert.IsEmpty(sessions.CreatedSessions);
        Assert.AreEqual(1, profiles.LoadCallCount);
    }

    [TestMethod]
    public async Task ExplicitInitializationRetryLoadsOriginalProfilesBeforeAddingAnother()
    {
        BackendProfile existing = CreateProfile("Existing", 9090);
        FakeBackendProfileStore profiles = new()
        {
            Current = new BackendProfileSet { Profiles = [existing] },
            LoadException = new IOException("The profile file is temporarily unavailable."),
        };
        FakeBackendCredentialStore credentials = new();
        credentials.Seed(existing.Id, "existing-secret");
        await using AppSessionCoordinator coordinator = CreateCoordinator(
            profiles, credentials, new FakeBackendSessionFactory());

        await coordinator.InitializeAsync();
        Assert.AreEqual(BackendProfilesLoadState.Failed, coordinator.ProfileLoadState);
        profiles.LoadException = null;

        await coordinator.InitializeAsync();

        Assert.AreEqual(2, profiles.LoadCallCount);
        Assert.IsTrue(coordinator.IsInitialized);
        Assert.IsTrue(coordinator.CanWriteProfiles);
        Assert.AreEqual(BackendProfilesLoadState.Loaded, coordinator.ProfileLoadState);
        Assert.IsNull(coordinator.ProfileLoadError);
        Assert.IsNull(coordinator.LastErrorMessage);
        Assert.AreEqual(existing.Id, coordinator.Profiles.Single().Id);
        Assert.IsTrue(coordinator.HasStoredCredential(existing.Id));

        Guid createdId = await coordinator.SaveAndConnectAsync(NewProfileRequest());

        Assert.HasCount(2, profiles.Current.Profiles);
        Assert.AreEqual(existing.Id, profiles.Current.Profiles[0].Id);
        Assert.AreEqual(createdId, profiles.Current.Profiles[1].Id);
        Assert.AreEqual(createdId, profiles.Current.ActiveProfileId);
        Assert.AreEqual("existing-secret", credentials.GetStored(existing.Id)?.Secret);
        Assert.HasCount(1, profiles.Saves);
    }

    [TestMethod]
    public async Task SuccessfullyLoadedEmptyProfileStoreAllowsFirstProfileWrite()
    {
        FakeBackendProfileStore profiles = new();
        await using AppSessionCoordinator coordinator = CreateCoordinator(
            profiles, new FakeBackendCredentialStore(), new FakeBackendSessionFactory());

        await coordinator.InitializeAsync();

        Assert.AreEqual(BackendProfilesLoadState.Empty, coordinator.ProfileLoadState);
        Assert.IsTrue(coordinator.IsInitialized);
        Assert.IsTrue(coordinator.CanWriteProfiles);

        Guid createdId = await coordinator.SaveAndConnectAsync(NewProfileRequest());

        Assert.AreEqual(BackendProfilesLoadState.Loaded, coordinator.ProfileLoadState);
        Assert.AreEqual(createdId, profiles.Current.Profiles.Single().Id);
    }

    private static AppSessionCoordinator CreateCoordinator(
        FakeBackendProfileStore profiles,
        FakeBackendCredentialStore credentials,
        FakeBackendSessionFactory sessions) => new(
            profiles,
            credentials,
            sessions,
            new InlineTestDispatcher(),
            new AppSettingsState(),
            TimeProvider.System);

    private static BackendProfile CreateProfile(string name, int port) => new(
        Guid.NewGuid(), name, BackendEndpoint.Create($"http://127.0.0.1:{port}"));

    private static BackendSaveRequest NewProfileRequest() => new(
        null,
        "New backend",
        new Uri("http://127.0.0.1:9091"),
        string.Empty,
        BackendCredentialUpdate.Remove);
}
