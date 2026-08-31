using System.Globalization;
using System.Text.Json;
using Zashboard.Core.Abstractions;
using Zashboard.Core.Backends;
using Zashboard.Core.Capabilities;
using Zashboard.Core.Clash;
using Zashboard.Infrastructure.Clash.Serialization;
using Zashboard.Infrastructure.Clash.Transport;
using Zashboard.Infrastructure.Clash.Wire;

namespace Zashboard.Infrastructure.Clash;

public sealed class ClashRestClient : IClashRestClient
{
    private static readonly TimeSpan DelayTransportGracePeriod = TimeSpan.FromSeconds(5);

    private readonly ClashHttpTransport _transport;
    private readonly ICapabilityRegistry _capabilities;
    private readonly TimeProvider _timeProvider;

    public ClashRestClient(
        HttpClient httpClient,
        BackendProfile profile,
        BackendCredential credential,
        ICapabilityRegistry capabilities,
        TimeProvider? timeProvider = null,
        ClashHttpOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _transport = new ClashHttpTransport(
            httpClient,
            profile.Endpoint,
            credential,
            capabilities,
            options ?? new ClashHttpOptions(),
            _timeProvider);
    }

    public async Task<ClashVersion> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        VersionResponseDto response = await GetAsync(
            ["version"],
            ClashJsonContext.Default.VersionResponseDto,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return ClashWireMapper.ToDomain(response);
    }

    public async Task<ProxyCatalog> GetProxiesAsync(CancellationToken cancellationToken = default)
    {
        ProxyCatalogDto response = await GetAsync(
            ["proxies"],
            ClashJsonContext.Default.ProxyCatalogDto,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        ProxyCatalog result = ClashWireMapper.ToDomain(response);
        if (result.Proxies.Values.Any(static proxy => proxy.Extra.Count > 0))
        {
            ObserveResponseCapability(ClashCapability.IndependentLatencyHistory);
        }

        return result;
    }

    public Task SelectProxyAsync(
        string groupName,
        string proxyName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);
        ArgumentException.ThrowIfNullOrWhiteSpace(proxyName);

        ProxySelectionRequestDto request = new() { Name = proxyName };
        return SendJsonAsync(
            HttpMethod.Put,
            ["proxies", groupName],
            request,
            ClashJsonContext.Default.ProxySelectionRequestDto,
            cancellationToken: cancellationToken);
    }

    public Task ClearFixedProxyAsync(string groupName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);
        return SendAsync(HttpMethod.Delete, ["proxies", groupName], cancellationToken: cancellationToken);
    }

    public async Task<ProxyDelay> MeasureProxyDelayAsync(
        string proxyName,
        ProxyDelayRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proxyName);
        ArgumentNullException.ThrowIfNull(request);

        DelayResponseDto response = await GetAsync(
            ["proxies", proxyName, "delay"],
            ClashJsonContext.Default.DelayResponseDto,
            DelayQuery(request),
            cancellationToken: cancellationToken,
            minimumTimeout: DelayOperationTimeout(request)).ConfigureAwait(false);

        return new ProxyDelay(response.Delay);
    }

    public async Task<ProxyDelay> MeasureProviderProxyDelayAsync(
        string providerName,
        string proxyName,
        ProxyDelayRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(proxyName);
        ArgumentNullException.ThrowIfNull(request);

        DelayResponseDto response = await GetAsync(
            ["providers", "proxies", providerName, proxyName, "healthcheck"],
            ClashJsonContext.Default.DelayResponseDto,
            query: DelayQuery(request),
            capability: ClashCapability.ProviderProxyHealthCheck,
            notFoundBehavior: CapabilityNotFoundBehavior.Inconclusive,
            cancellationToken: cancellationToken,
            minimumTimeout: DelayOperationTimeout(request)).ConfigureAwait(false);

        return new ProxyDelay(response.Delay);
    }

    public async Task<IReadOnlyDictionary<string, ProxyDelay>> MeasureProxyGroupDelayAsync(
        string groupName,
        ProxyDelayRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);
        ArgumentNullException.ThrowIfNull(request);

        Dictionary<string, int> response = await GetAsync(
            ["group", groupName, "delay"],
            ClashJsonContext.Default.DictionaryStringInt32,
            DelayQuery(request),
            cancellationToken: cancellationToken,
            minimumTimeout: DelayOperationTimeout(request)).ConfigureAwait(false);

        return response.ToDictionary(
            static pair => pair.Key,
            static pair => new ProxyDelay(pair.Value),
            StringComparer.Ordinal);
    }

    public async Task<ProxyProviderCatalog> GetProxyProvidersAsync(
        CancellationToken cancellationToken = default)
    {
        ProxyProviderCatalogDto response = await GetAsync(
            ["providers", "proxies"],
            ClashJsonContext.Default.ProxyProviderCatalogDto,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return ClashWireMapper.ToDomain(response);
    }

    public Task UpdateProxyProviderAsync(
        string providerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        return SendAsync(
            HttpMethod.Put,
            ["providers", "proxies", providerName],
            capability: ClashCapability.ProviderProxyUpdate,
            cancellationToken: cancellationToken,
            notFoundBehavior: CapabilityNotFoundBehavior.Inconclusive);
    }

    public Task CheckProxyProviderAsync(
        string providerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        return SendAsync(
            HttpMethod.Get,
            ["providers", "proxies", providerName, "healthcheck"],
            capability: ClashCapability.ProviderProxyHealthCheck,
            cancellationToken: cancellationToken,
            notFoundBehavior: CapabilityNotFoundBehavior.Inconclusive);
    }

    public async Task<RuleCatalog> GetRulesAsync(CancellationToken cancellationToken = default)
    {
        RuleCatalogDto response = await GetAsync(
            ["rules"],
            ClashJsonContext.Default.RuleCatalogDto,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        RuleCatalog result = ClashWireMapper.ToDomain(response);
        if (result.Rules.Any(static rule => rule.Index.HasValue))
        {
            ObserveResponseCapability(ClashCapability.RuleDisableByIndex);
        }

        if (result.Rules.Any(static rule => !string.IsNullOrWhiteSpace(rule.Identifier)))
        {
            ObserveResponseCapability(ClashCapability.RuleDisableByIdentifier);
        }

        return result;
    }

    public async Task<RuleProviderCatalog> GetRuleProvidersAsync(
        CancellationToken cancellationToken = default)
    {
        RuleProviderCatalogDto response = await GetAsync(
            ["providers", "rules"],
            ClashJsonContext.Default.RuleProviderCatalogDto,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return ClashWireMapper.ToDomain(response);
    }

    public Task UpdateRuleProviderAsync(
        string providerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        return SendAsync(
            HttpMethod.Put,
            ["providers", "rules", providerName],
            cancellationToken: cancellationToken);
    }

    public Task SetRuleDisabledStatesAsync(
        IReadOnlyDictionary<int, bool> disabledStates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disabledStates);

        Dictionary<string, bool> request = new(disabledStates.Count, StringComparer.Ordinal);
        foreach ((int index, bool disabled) in disabledStates)
        {
            request[index.ToString(CultureInfo.InvariantCulture)] = disabled;
        }

        return SendJsonAsync(
            HttpMethod.Patch,
            ["rules", "disable"],
            request,
            ClashJsonContext.Default.DictionaryStringBoolean,
            capability: ClashCapability.RuleDisableByIndex,
            cancellationToken: cancellationToken);
    }

    public Task ToggleRuleDisabledByIdentifierAsync(
        string identifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return SendAsync(
            HttpMethod.Put,
            ["rules", identifier],
            capability: ClashCapability.RuleDisableByIdentifier,
            cancellationToken: cancellationToken,
            notFoundBehavior: CapabilityNotFoundBehavior.Inconclusive);
    }

    public Task CloseConnectionAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        return SendAsync(
            HttpMethod.Delete,
            ["connections", connectionId],
            cancellationToken: cancellationToken);
    }

    public Task CloseAllConnectionsAsync(CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, ["connections"], cancellationToken: cancellationToken);

    public Task BlockSmartConnectionAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        return SendAsync(
            HttpMethod.Delete,
            ["connections", "smart", connectionId],
            capability: ClashCapability.SmartConnectionBlock,
            cancellationToken: cancellationToken,
            notFoundBehavior: CapabilityNotFoundBehavior.Inconclusive);
    }

    public async Task<ClashConfiguration> GetConfigurationAsync(
        CancellationToken cancellationToken = default)
    {
        ConfigurationDto response = await GetAsync(
            ["configs"],
            ClashJsonContext.Default.ConfigurationDto,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return ClashWireMapper.ToDomain(response);
    }

    public Task PatchConfigurationAsync(
        ClashConfigurationPatch patch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ConfigurationPatchDto request = ClashWireMapper.ToWire(patch);
        return SendJsonAsync(
            HttpMethod.Patch,
            ["configs"],
            request,
            ClashJsonContext.Default.ConfigurationPatchDto,
            capability: ClashCapability.ConfigurationPatch,
            cancellationToken: cancellationToken);
    }

    public Task ReloadConfigurationAsync(CancellationToken cancellationToken = default)
    {
        ConfigurationUpdateDto request = new() { Path = string.Empty, Payload = string.Empty };
        return SendJsonAsync(
            HttpMethod.Put,
            ["configs"],
            request,
            ClashJsonContext.Default.ConfigurationUpdateDto,
            capability: ClashCapability.ConfigurationReload,
            query: [ClashUriFactory.Query("reload", true)],
            cancellationToken: cancellationToken);
    }

    public Task UpdateConfigurationAsync(
        ClashConfigurationUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ConfigurationUpdateDto request = ClashWireMapper.ToWire(update);
        IReadOnlyList<KeyValuePair<string, string?>>? query = update.Force
            ? [ClashUriFactory.Query("force", true)]
            : null;

        return SendJsonAsync(
            HttpMethod.Put,
            ["configs"],
            request,
            ClashJsonContext.Default.ConfigurationUpdateDto,
            capability: ClashCapability.ConfigurationUpdate,
            query: query,
            cancellationToken: cancellationToken);
    }

    public Task FlushFakeIpCacheAsync(CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, ["cache", "fakeip", "flush"], cancellationToken: cancellationToken);

    public Task FlushDnsCacheAsync(CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, ["cache", "dns", "flush"], cancellationToken: cancellationToken);

    public async Task<DnsQueryResult> QueryDnsAsync(
        DnsQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        DnsQueryResponseDto response = await GetAsync(
            ["dns", "query"],
            ClashJsonContext.Default.DnsQueryResponseDto,
            [
                ClashUriFactory.Query("name", request.Name),
                ClashUriFactory.Query("type", request.Type),
            ],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return ClashWireMapper.ToDomain(response);
    }

    public Task UpdateGeoDataAsync(CancellationToken cancellationToken = default) =>
        SendAsync(
            HttpMethod.Post,
            ["configs", "geo"],
            capability: ClashCapability.GeoDataUpdate,
            cancellationToken: cancellationToken);

    public Task UpgradeCoreAsync(
        CoreUpgradeChannel channel,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<KeyValuePair<string, string?>>? query = channel switch
        {
            CoreUpgradeChannel.Auto => null,
            CoreUpgradeChannel.Release => [ClashUriFactory.Query("channel", "release")],
            CoreUpgradeChannel.Alpha => [ClashUriFactory.Query("channel", "alpha")],
            _ => throw new ArgumentOutOfRangeException(nameof(channel)),
        };

        return SendAsync(
            HttpMethod.Post,
            ["upgrade"],
            capability: ClashCapability.CoreUpgrade,
            query: query,
            cancellationToken: cancellationToken);
    }

    public Task RestartCoreAsync(CancellationToken cancellationToken = default) =>
        SendAsync(
            HttpMethod.Post,
            ["restart"],
            capability: ClashCapability.CoreRestart,
            cancellationToken: cancellationToken);

    public Task UpgradeDashboardAsync(CancellationToken cancellationToken = default) =>
        SendAsync(
            HttpMethod.Post,
            ["upgrade", "ui"],
            capability: ClashCapability.DashboardUpgrade,
            cancellationToken: cancellationToken);

    public async Task<DashboardStorage> GetDashboardStorageAsync(
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, JsonElement> response = await GetAsync(
            ["storage", "zashboard"],
            ClashJsonContext.Default.DictionaryStringJsonElement,
            capability: ClashCapability.SettingsStorage,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return ClashWireMapper.ToDomain(response);
    }

    public Task SetDashboardStorageAsync(
        DashboardStorage storage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        Dictionary<string, string> request = storage.Values.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
        return SendJsonAsync(
            HttpMethod.Put,
            ["storage", "zashboard"],
            request,
            ClashJsonContext.Default.DictionaryStringString,
            capability: ClashCapability.SettingsStorage,
            cancellationToken: cancellationToken);
    }

    public Task DeleteDashboardStorageAsync(CancellationToken cancellationToken = default) =>
        SendAsync(
            HttpMethod.Delete,
            ["storage", "zashboard"],
            capability: ClashCapability.SettingsStorage,
            cancellationToken: cancellationToken);

    public async Task<SmartWeights> GetSmartWeightsAsync(CancellationToken cancellationToken = default)
    {
        SmartWeightsDto response = await GetAsync(
            ["group", "weights"],
            ClashJsonContext.Default.SmartWeightsDto,
            capability: ClashCapability.SmartWeights,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return ClashWireMapper.ToDomain(response);
    }

    public Task FlushSmartWeightsAsync(CancellationToken cancellationToken = default) =>
        SendAsync(
            HttpMethod.Post,
            ["cache", "smart", "flush"],
            capability: ClashCapability.SmartWeightReset,
            cancellationToken: cancellationToken);

    public async Task<HonkRuntimeStatistics> GetHonkRuntimeStatisticsAsync(
        CancellationToken cancellationToken = default)
    {
        HonkRuntimeStatisticsDto response = await GetAsync(
            ["stats"],
            ClashJsonContext.Default.HonkRuntimeStatisticsDto,
            capability: ClashCapability.RuntimeStatistics,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return ClashWireMapper.ToDomain(response);
    }

    private async Task<T> GetAsync<T>(
        string[] pathSegments,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        IReadOnlyList<KeyValuePair<string, string?>>? query = null,
        ClashCapability? capability = null,
        CapabilityNotFoundBehavior notFoundBehavior = CapabilityNotFoundBehavior.Unsupported,
        TimeSpan? minimumTimeout = null,
        CancellationToken cancellationToken = default)
    {
        Uri uri = _transport.BuildUri(pathSegments, query);
        return await _transport.SendAsync(
            HttpMethod.Get,
            uri,
            typeInfo,
            capability: capability,
            notFoundBehavior: notFoundBehavior,
            cancellationToken: cancellationToken,
            minimumTimeout: minimumTimeout).ConfigureAwait(false);
    }

    private Task SendAsync(
        HttpMethod method,
        string[] pathSegments,
        ClashCapability? capability = null,
        IReadOnlyList<KeyValuePair<string, string?>>? query = null,
        CapabilityNotFoundBehavior notFoundBehavior = CapabilityNotFoundBehavior.Unsupported,
        CancellationToken cancellationToken = default)
    {
        Uri uri = _transport.BuildUri(pathSegments, query);
        return _transport.SendAsync(
            method,
            uri,
            capability: capability,
            notFoundBehavior: notFoundBehavior,
            cancellationToken: cancellationToken);
    }

    private async Task SendJsonAsync<T>(
        HttpMethod method,
        string[] pathSegments,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        ClashCapability? capability = null,
        IReadOnlyList<KeyValuePair<string, string?>>? query = null,
        CancellationToken cancellationToken = default)
    {
        Uri uri = _transport.BuildUri(pathSegments, query);
        using HttpContent content = JsonHttpContent.Create(value, typeInfo);
        await _transport.SendAsync(
            method,
            uri,
            content,
            capability,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<KeyValuePair<string, string?>> DelayQuery(ProxyDelayRequest request) =>
    [
        ClashUriFactory.Query("url", request.TargetUrl.AbsoluteUri),
        ClashUriFactory.Query("timeout", ToTimeoutMilliseconds(request.Timeout)),
    ];

    private static int ToTimeoutMilliseconds(TimeSpan timeout) =>
        (int)Math.Clamp((long)Math.Ceiling(timeout.TotalMilliseconds), 1, int.MaxValue);

    private static TimeSpan DelayOperationTimeout(ProxyDelayRequest request) =>
        TimeSpan.FromMilliseconds(ToTimeoutMilliseconds(request.Timeout)) +
        DelayTransportGracePeriod;

    private void ObserveResponseCapability(ClashCapability capability) =>
        _capabilities.Observe(
            capability,
            CapabilitySupport.Supported,
            CapabilityEvidenceKind.ResponseData,
            _timeProvider.GetUtcNow());
}
