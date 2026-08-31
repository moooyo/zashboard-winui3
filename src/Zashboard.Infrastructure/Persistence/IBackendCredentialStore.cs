using Zashboard.Core.Backends;

namespace Zashboard.Infrastructure.Persistence;

public interface IBackendCredentialStore
{
    async ValueTask<bool> ExistsAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        BackendCredential? credential = await GetAsync(profileId, cancellationToken)
            .ConfigureAwait(false);
        return credential is not null;
    }

    ValueTask<BackendCredential?> GetAsync(
        Guid profileId,
        CancellationToken cancellationToken = default);

    ValueTask SetAsync(
        Guid profileId,
        BackendCredential credential,
        CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(Guid profileId, CancellationToken cancellationToken = default);
}
