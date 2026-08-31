using Zashboard.Core.Backends;

namespace Zashboard.Core.Abstractions;

public interface IBackendProbe
{
    Task<BackendProbeResult> ProbeAsync(
        BackendProfile profile,
        BackendCredential credential,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
