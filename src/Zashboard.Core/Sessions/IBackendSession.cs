using Zashboard.Core.Abstractions;
using Zashboard.Core.Backends;
using Zashboard.Core.Capabilities;

namespace Zashboard.Core.Sessions;

public interface IBackendSession : IAsyncDisposable
{
    SessionEpoch Epoch { get; }

    BackendProfile Profile { get; }

    IClashRestClient RestClient { get; }

    IClashStreamClient StreamClient { get; }

    ICapabilityRegistry Capabilities { get; }

    CancellationToken Lifetime { get; }

    BackendSessionSnapshot Snapshot { get; }
}

public interface IBackendSessionFactory
{
    ValueTask<IBackendSession> CreateAsync(
        BackendProfile profile,
        BackendCredential credential,
        SessionEpoch epoch,
        CancellationToken cancellationToken = default);
}
