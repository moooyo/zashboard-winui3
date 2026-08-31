using Zashboard.Core.Abstractions;
using Zashboard.Core.Backends;
using Zashboard.Core.Capabilities;
using Zashboard.Core.Sessions;

namespace Zashboard.Infrastructure.Sessions;

internal sealed class BackendSession : IBackendSession
{
    private readonly HttpClient _httpClient;
    private readonly CancellationTokenSource _lifetimeSource;
    private BackendSessionSnapshot _snapshot;
    private int _disposeState;

    public BackendSession(
        SessionEpoch epoch,
        BackendProfile profile,
        HttpClient httpClient,
        CancellationTokenSource lifetimeSource,
        IClashRestClient restClient,
        IClashStreamClient streamClient,
        ICapabilityRegistry capabilities,
        BackendSessionSnapshot snapshot)
    {
        Epoch = epoch;
        Profile = profile;
        _httpClient = httpClient;
        _lifetimeSource = lifetimeSource;
        RestClient = restClient;
        StreamClient = streamClient;
        Capabilities = capabilities;
        _snapshot = snapshot;
    }

    public SessionEpoch Epoch { get; }

    public BackendProfile Profile { get; }

    public IClashRestClient RestClient { get; }

    public IClashStreamClient StreamClient { get; }

    public ICapabilityRegistry Capabilities { get; }

    public CancellationToken Lifetime => _lifetimeSource.Token;

    public BackendSessionSnapshot Snapshot => Volatile.Read(ref _snapshot) with
    {
        Capabilities = Capabilities.GetSnapshot(),
    };

    public void UpdateSnapshot(BackendSessionSnapshot snapshot)
    {
        if (!snapshot.BelongsTo(Epoch))
        {
            throw new ArgumentException("The snapshot belongs to a different session epoch.", nameof(snapshot));
        }

        Volatile.Write(ref _snapshot, snapshot);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        await _lifetimeSource.CancelAsync().ConfigureAwait(false);
        _httpClient.CancelPendingRequests();
        _httpClient.Dispose();
        _lifetimeSource.Dispose();
    }
}
