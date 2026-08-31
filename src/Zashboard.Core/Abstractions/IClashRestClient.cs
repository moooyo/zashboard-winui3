using Zashboard.Core.Clash;

namespace Zashboard.Core.Abstractions;

public interface IClashRestClient
{
    Task<ClashVersion> GetVersionAsync(CancellationToken cancellationToken = default);

    Task<ProxyCatalog> GetProxiesAsync(CancellationToken cancellationToken = default);

    Task SelectProxyAsync(
        string groupName,
        string proxyName,
        CancellationToken cancellationToken = default);

    Task ClearFixedProxyAsync(string groupName, CancellationToken cancellationToken = default);

    Task<ProxyDelay> MeasureProxyDelayAsync(
        string proxyName,
        ProxyDelayRequest request,
        CancellationToken cancellationToken = default);

    Task<ProxyDelay> MeasureProviderProxyDelayAsync(
        string providerName,
        string proxyName,
        ProxyDelayRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, ProxyDelay>> MeasureProxyGroupDelayAsync(
        string groupName,
        ProxyDelayRequest request,
        CancellationToken cancellationToken = default);

    Task<ProxyProviderCatalog> GetProxyProvidersAsync(CancellationToken cancellationToken = default);

    Task UpdateProxyProviderAsync(string providerName, CancellationToken cancellationToken = default);

    Task CheckProxyProviderAsync(
        string providerName,
        CancellationToken cancellationToken = default);

    Task<RuleCatalog> GetRulesAsync(CancellationToken cancellationToken = default);

    Task<RuleProviderCatalog> GetRuleProvidersAsync(CancellationToken cancellationToken = default);

    Task UpdateRuleProviderAsync(string providerName, CancellationToken cancellationToken = default);

    Task SetRuleDisabledStatesAsync(
        IReadOnlyDictionary<int, bool> disabledStates,
        CancellationToken cancellationToken = default);

    Task ToggleRuleDisabledByIdentifierAsync(
        string identifier,
        CancellationToken cancellationToken = default);

    Task CloseConnectionAsync(string connectionId, CancellationToken cancellationToken = default);

    Task CloseAllConnectionsAsync(CancellationToken cancellationToken = default);

    Task BlockSmartConnectionAsync(string connectionId, CancellationToken cancellationToken = default);

    Task<ClashConfiguration> GetConfigurationAsync(CancellationToken cancellationToken = default);

    Task PatchConfigurationAsync(
        ClashConfigurationPatch patch,
        CancellationToken cancellationToken = default);

    Task ReloadConfigurationAsync(CancellationToken cancellationToken = default);

    Task UpdateConfigurationAsync(
        ClashConfigurationUpdate update,
        CancellationToken cancellationToken = default);

    Task FlushFakeIpCacheAsync(CancellationToken cancellationToken = default);

    Task FlushDnsCacheAsync(CancellationToken cancellationToken = default);

    Task<DnsQueryResult> QueryDnsAsync(
        DnsQueryRequest request,
        CancellationToken cancellationToken = default);

    Task UpdateGeoDataAsync(CancellationToken cancellationToken = default);

    Task UpgradeCoreAsync(
        CoreUpgradeChannel channel,
        CancellationToken cancellationToken = default);

    Task RestartCoreAsync(CancellationToken cancellationToken = default);

    Task UpgradeDashboardAsync(CancellationToken cancellationToken = default);

    Task<DashboardStorage> GetDashboardStorageAsync(CancellationToken cancellationToken = default);

    Task SetDashboardStorageAsync(
        DashboardStorage storage,
        CancellationToken cancellationToken = default);

    Task DeleteDashboardStorageAsync(CancellationToken cancellationToken = default);

    Task<SmartWeights> GetSmartWeightsAsync(CancellationToken cancellationToken = default);

    Task FlushSmartWeightsAsync(CancellationToken cancellationToken = default);

    Task<HonkRuntimeStatistics> GetHonkRuntimeStatisticsAsync(
        CancellationToken cancellationToken = default);
}
