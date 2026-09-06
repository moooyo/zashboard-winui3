using System.Net;
using Zashboard.Core.Backends;
using Zashboard.Core.Clash;
using Zashboard.Core.Sessions;
using Zashboard.Infrastructure.Clash;

namespace Zashboard.App.Services;

public sealed partial class AppSessionCoordinator
{
    private static readonly TimeSpan ResourceRecoveryInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumResourceRecoveryInterval = TimeSpan.FromSeconds(30);
    private readonly Lock _resourceRequestGate = new();
    private readonly Dictionary<string, ResourceRequest> _resourceRequests = new(StringComparer.Ordinal);
    private readonly HashSet<ResourceRequest> _pendingResourceRequests = [];
    private readonly HashSet<string> _retryableResourceFailures = new(StringComparer.Ordinal);
    private long _nextResourceRequestVersion;

    private readonly record struct ResourceRequest(SessionEpoch Epoch, string Name, long Version);

    private ResourceRequest BeginResourceRequest(IBackendSession session, string name)
    {
        lock (_resourceRequestGate)
        {
            ResourceRequest request = new(session.Epoch, name, ++_nextResourceRequestVersion);
            if (session.Epoch == Epoch)
            {
                _resourceRequests[name] = request;
                _pendingResourceRequests.Add(request);
            }

            return request;
        }
    }

    private ValueTask DispatchResourceAsync(ResourceRequest request, Action action) =>
        DispatchForEpochAsync(request.Epoch, () =>
        {
            lock (_resourceRequestGate)
            {
                if (_resourceRequests.TryGetValue(request.Name, out ResourceRequest current) &&
                    current == request)
                {
                    action();
                }
            }
        });

    private async Task<bool> FetchResourceAsync<T>(
        IBackendSession session,
        string name,
        Func<CancellationToken, Task<T>> load,
        Action<T> publish,
        bool throwOnFailure,
        bool trackFailure,
        CancellationToken cancellationToken)
    {
        ResourceRequest request = BeginResourceRequest(session, name);
        try
        {
            T value = await load(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            bool published = false;
            await DispatchResourceAsync(request, () =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                _resourceFailures.Remove(name);
                _retryableResourceFailures.Remove(name);
                publish(value);
                MarkLiveContact();
                published = true;
            }).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return published;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RecoverMissingResourceAsync(request, trackFailure).ConfigureAwait(false);
            if (throwOnFailure)
            {
                throw;
            }

            return false;
        }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                await RecoverMissingResourceAsync(request, trackFailure).ConfigureAwait(false);
                if (throwOnFailure)
                {
                    throw new OperationCanceledException("The resource request was canceled.", exception, cancellationToken);
                }

                return false;
            }

            bool currentFailure = false;
            await DispatchResourceAsync(request, () =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                currentFailure = true;
                if (trackFailure || exception is ClashAuthenticationException)
                {
                    ApplyFailure(session, name, exception, isResourceFailure: trackFailure);
                }
            }).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                await RecoverMissingResourceAsync(request, trackFailure).ConfigureAwait(false);
                if (throwOnFailure)
                {
                    throw new OperationCanceledException("The resource request was canceled.", exception, cancellationToken);
                }

                return false;
            }

            if (throwOnFailure && currentFailure)
            {
                throw;
            }

            return false;
        }
        finally
        {
            lock (_resourceRequestGate)
            {
                _pendingResourceRequests.Remove(request);
            }
        }
    }

    private ValueTask RecoverMissingResourceAsync(ResourceRequest request, bool trackFailure) =>
        !trackFailure || _shutdownSource.IsCancellationRequested
            ? ValueTask.CompletedTask
            : DispatchResourceAsync(request, () =>
            {
                bool missing = request.Name switch
                {
                    "version" => string.IsNullOrWhiteSpace(SessionSnapshot.Version),
                    "configuration" => Configuration is null,
                    "proxies" => ProxyCatalog is null,
                    "proxy providers" => ProxyProviders is null,
                    "rules" => RuleCatalog is null,
                    "rule providers" => RuleProviders is null,
                    _ => false,
                };
                if (missing && SessionSnapshot.State != BackendConnectionState.Unauthorized)
                {
                    _resourceFailures[request.Name] =
                        $"The {request.Name} load was canceled before any data was available. Retrying.";
                    _retryableResourceFailures.Add(request.Name);
                    MarkLiveContact();
                }
            });

    private Task<bool> RefreshVersionAsync(IBackendSession session, CancellationToken cancellationToken) =>
        LoadResourceAsync(session, "version", session.RestClient.GetVersionAsync, version =>
        {
            SessionSnapshot = SessionSnapshot with
            {
                Version = version.Value,
                CoreKind = version.CoreKind,
            };
        }, cancellationToken);

    private async Task PumpResourceRecoveryAsync(IBackendSession session, CancellationToken cancellationToken)
    {
        TimeSpan interval = ResourceRecoveryInterval;
        try
        {
            while (true)
            {
                await Task.Delay(interval, _timeProvider, cancellationToken).ConfigureAwait(false);
                string[] failures = [];
                bool unauthorized = false;
                await DispatchForEpochAsync(session.Epoch, () =>
                {
                    unauthorized = SessionSnapshot.State == BackendConnectionState.Unauthorized;
                    if (unauthorized || IsUserOperationRunning)
                    {
                        return;
                    }

                    lock (_resourceRequestGate)
                    {
                        failures = _retryableResourceFailures.Where(name =>
                            !_resourceRequests.TryGetValue(name, out ResourceRequest request) ||
                            !_pendingResourceRequests.Contains(request)).ToArray();
                    }
                }).ConfigureAwait(false);
                if (unauthorized)
                {
                    return;
                }

                if (failures.Length == 0)
                {
                    interval = ResourceRecoveryInterval;
                    continue;
                }

                Task<bool>[] retries = failures.Select(name => name switch
                {
                    "version" => RefreshVersionAsync(session, cancellationToken),
                    "configuration" => LoadResourceAsync(session, name,
                        session.RestClient.GetConfigurationAsync, value => Configuration = value, cancellationToken),
                    "proxies" => LoadResourceAsync(session, name,
                        session.RestClient.GetProxiesAsync, value => ProxyCatalog = value, cancellationToken),
                    "proxy providers" => LoadResourceAsync(session, name,
                        session.RestClient.GetProxyProvidersAsync, value => ProxyProviders = value, cancellationToken),
                    "rules" => LoadResourceAsync(session, name,
                        session.RestClient.GetRulesAsync, value => RuleCatalog = value, cancellationToken),
                    "rule providers" => LoadResourceAsync(session, name,
                        session.RestClient.GetRuleProvidersAsync, value => RuleProviders = value, cancellationToken),
                    _ => Task.FromResult(false),
                }).ToArray();
                bool[] results = await Task.WhenAll(retries).ConfigureAwait(false);
                interval = results.Any(static succeeded => succeeded)
                    ? ResourceRecoveryInterval
                    : TimeSpan.FromTicks(Math.Min(interval.Ticks * 2, MaximumResourceRecoveryInterval.Ticks));
                if (results.Any(static succeeded => succeeded))
                {
                    await TryRefreshSmartWeightsAsync(session, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static bool IsRetryableResourceFailure(Exception exception) => exception switch
    {
        ClashAuthenticationException or ClashProtocolException or NotSupportedException or ArgumentException => false,
        ClashApiException { StatusCode: { } status } =>
            status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500,
        _ => true,
    };
}
