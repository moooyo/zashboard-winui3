using System.Net;
using Zashboard.Core.Abstractions;
using Zashboard.Core.Backends;
using Zashboard.Infrastructure.Clash.Transport;

namespace Zashboard.Infrastructure.Clash;

public sealed class ClashBackendProbe : IBackendProbe
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeProvider _timeProvider;

    public ClashBackendProbe(IHttpClientFactory httpClientFactory, TimeProvider? timeProvider = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<BackendProbeResult> ProbeAsync(
        BackendProfile profile,
        BackendCredential credential,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        using CancellationTokenSource timeoutSource = new(timeout, _timeProvider);
        using CancellationTokenSource linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        using HttpRequestMessage request = new(
            HttpMethod.Get,
            ClashUriFactory.Http(profile.Endpoint, ["version"]));
        ClashAuthentication.ApplyBearer(request.Headers, credential.Secret);

        long startedAt = _timeProvider.GetTimestamp();
        try
        {
            using HttpClient httpClient = _httpClientFactory.CreateClient(
                InfrastructureServiceCollectionExtensions.ClashHttpClientName);
            using HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                linkedSource.Token).ConfigureAwait(false);

            TimeSpan latency = _timeProvider.GetElapsedTime(startedAt);
            if (response.IsSuccessStatusCode)
            {
                return BackendProbeResult.Success(latency);
            }

            BackendProbeFailureKind kind = response.StatusCode is
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? BackendProbeFailureKind.Unauthorized
                : BackendProbeFailureKind.Http;
            return BackendProbeResult.Failure(
                latency,
                kind,
                $"HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return BackendProbeResult.Failure(
                _timeProvider.GetElapsedTime(startedAt),
                BackendProbeFailureKind.Timeout,
                "The backend probe timed out.");
        }
        catch (HttpRequestException exception)
        {
            BackendProbeFailureKind kind = exception.StatusCode.HasValue
                ? BackendProbeFailureKind.Http
                : BackendProbeFailureKind.Network;
            return BackendProbeResult.Failure(
                _timeProvider.GetElapsedTime(startedAt),
                kind,
                exception.Message);
        }
    }
}
