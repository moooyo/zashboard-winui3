namespace Zashboard.Infrastructure.Persistence;

public interface IBackendProfileStore
{
    ValueTask<BackendProfileSet> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(
        BackendProfileSet profiles,
        CancellationToken cancellationToken = default);
}
