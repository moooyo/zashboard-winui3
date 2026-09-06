using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.Input;
using Zashboard.App.Controls;
using Zashboard.App.Services;
using Zashboard.Core.Backends;

namespace Zashboard.App.ViewModels;

public sealed partial class BackendSetupViewModel : ViewModelBase
{
    private Guid? _renderedActiveProfileId;
    private BackendConnectionState _renderedSessionState;

    public BackendSetupViewModel(AppSessionCoordinator coordinator)
        : base(coordinator)
    {
        ConnectCommand = new AsyncRelayCommand<BackendSaveRequest>(ExecuteConnectCommandAsync);
        ActivateCommand = new AsyncRelayCommand<string>(ExecuteActivateCommandAsync);
        RemoveCommand = new AsyncRelayCommand<string>(ExecuteRemoveCommandAsync);
        ((INotifyCollectionChanged)Coordinator.Profiles).CollectionChanged +=
            OnProfilesCollectionChanged;
        RefreshBackends();
    }

    public ObservableCollection<BackendDisplayItem> SavedBackends { get; } =
        new BulkObservableCollection<BackendDisplayItem>();

    public IAsyncRelayCommand<BackendSaveRequest> ConnectCommand { get; }

    public IAsyncRelayCommand<string> ActivateCommand { get; }

    public IAsyncRelayCommand<string> RemoveCommand { get; }

    public async Task<Guid?> ConnectAsync(
        string name,
        Uri controllerUri,
        string secret,
        Guid? profileId,
        BackendCredentialUpdate credentialUpdate,
        CancellationToken cancellationToken = default)
    {
        Guid? savedProfileId = null;
        await ExecuteAsync(
            async token =>
            {
                savedProfileId = await Coordinator.SaveAndConnectAsync(
                    new BackendSaveRequest(
                        profileId,
                        name,
                        controllerUri,
                        secret,
                        credentialUpdate),
                    token);
            },
            allowCancellation: false,
            cancellationToken);
        return savedProfileId;
    }

    public Task ActivateAsync(
        string backendId,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            token => Coordinator.ActivateProfileAsync(ParseProfileId(backendId), token),
            allowCancellation: false,
            cancellationToken);

    public async Task<bool> RemoveAsync(
        string backendId,
        CancellationToken cancellationToken = default)
    {
        bool removed = false;
        await ExecuteAsync(
            async token =>
            {
                Guid profileId = ParseProfileId(backendId);
                bool existed = Coordinator.Profiles.Any(profile => profile.Id == profileId);
                await Coordinator.RemoveProfileAsync(profileId, token);
                removed = existed && Coordinator.Profiles.All(profile => profile.Id != profileId);
            },
            allowCancellation: false,
            cancellationToken);
        return removed;
    }

    protected override void HandleCoordinatorPropertyChanged(string? propertyName)
    {
        if (propertyName is nameof(AppSessionCoordinator.ActiveProfile) or
            nameof(AppSessionCoordinator.SessionSnapshot))
        {
            Guid? activeProfileId = Coordinator.ActiveProfile?.Id;
            BackendConnectionState state = Coordinator.SessionSnapshot.State;
            if (_renderedActiveProfileId != activeProfileId ||
                _renderedSessionState != state)
            {
                RefreshBackends();
            }
        }
    }

    protected override void DisposeCore()
    {
        ((INotifyCollectionChanged)Coordinator.Profiles).CollectionChanged -=
            OnProfilesCollectionChanged;
    }

    private async Task ExecuteConnectCommandAsync(BackendSaveRequest? request)
    {
        if (request is not null)
        {
            await ExecuteAsync(async token =>
            {
                _ = await Coordinator.SaveAndConnectAsync(request, token);
            });
        }
    }

    private Task ExecuteActivateCommandAsync(string? backendId) => string.IsNullOrWhiteSpace(backendId)
        ? Task.CompletedTask
        : ActivateAsync(backendId);

    private async Task ExecuteRemoveCommandAsync(string? backendId)
    {
        if (!string.IsNullOrWhiteSpace(backendId))
        {
            _ = await RemoveAsync(backendId);
        }
    }

    private void OnProfilesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) =>
        RefreshBackends();

    private void RefreshBackends()
    {
        _renderedActiveProfileId = Coordinator.ActiveProfile?.Id;
        _renderedSessionState = Coordinator.SessionSnapshot.State;
        BackendDisplayItem[] items = Coordinator.Profiles
            .Select(profile => DisplayModelFactory.Backend(
                profile,
                Coordinator.ActiveProfile,
                Coordinator.SessionSnapshot.State,
                Coordinator.HasStoredCredential(profile.Id)))
            .ToArray();
        ReplaceCollection(SavedBackends, items);
    }

    private static Guid ParseProfileId(string backendId)
    {
        if (!Guid.TryParse(backendId, out Guid profileId) || profileId == Guid.Empty)
        {
            throw new ArgumentException("The backend profile identifier is invalid.", nameof(backendId));
        }

        return profileId;
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> destination, IEnumerable<T> source)
    {
        CollectionBatch.Replace(destination, source);
    }
}
