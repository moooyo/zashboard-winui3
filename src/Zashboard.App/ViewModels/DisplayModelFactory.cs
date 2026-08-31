using System.Globalization;
using Zashboard.App.Controls;
using Zashboard.App.Services;
using Zashboard.Core.Backends;
using Zashboard.Core.Clash;
using Zashboard.Core.Normalization;

namespace Zashboard.App.ViewModels;

internal static class DisplayModelFactory
{
    public static BackendDisplayItem Backend(
        BackendProfile profile,
        BackendProfile? activeProfile,
        BackendConnectionState state,
        bool hasStoredCredential)
    {
        string status = activeProfile?.Id == profile.Id
            ? FormatState(state)
            : "Saved";
        return new BackendDisplayItem(
            profile.Id.ToString("D", CultureInfo.InvariantCulture),
            profile.Name,
            profile.Endpoint.BaseUri.AbsoluteUri,
            status,
            activeProfile?.Id == profile.Id,
            hasStoredCredential);
    }

    public static ProxyGroupDisplayItem ProxyGroup(ClashProxy proxy)
    {
        bool hasFixedProxy = !string.IsNullOrWhiteSpace(proxy.Fixed);
        return new ProxyGroupDisplayItem(
            proxy.Name,
            EmptyFallback(proxy.Type),
            EmptyFallback(hasFixedProxy ? proxy.Fixed : proxy.Now),
            proxy.All.Count,
            EmptyFallback(proxy.Fixed),
            hasFixedProxy,
            proxy.Kind == ClashProxyKind.Smart ||
                proxy.Type.Equals("Smart", StringComparison.OrdinalIgnoreCase));
    }

    public static ProxyNodeDisplayItem ProxyNode(ClashProxy proxy, Uri? latencyTarget = null)
    {
        ProxyIndependentHistory? independentHistory = FindIndependentHistory(
            proxy,
            latencyTarget);
        IReadOnlyList<ProxyHistoryEntry> history = independentHistory?.History ?? proxy.History;
        ProxyHistoryEntry? latest = history.Count == 0 ? null : history[^1];
        string latency = latest is null || latest.Delay <= 0
            ? "--"
            : $"{latest.Delay.ToString(CultureInfo.CurrentCulture)} ms";
        bool? alive = independentHistory is null ? proxy.Alive : independentHistory.Alive;
        string availability = alive switch
        {
            true => "Available",
            false => "Unavailable",
            null => "Unknown",
        };

        return new ProxyNodeDisplayItem(
            proxy.Name,
            EmptyFallback(proxy.Type),
            latency,
            availability,
            EmptyFallback(proxy.ProviderName),
            "--",
            "--",
            false,
            false,
            false,
            false,
            false);
    }

    private static ProxyIndependentHistory? FindIndependentHistory(
        ClashProxy proxy,
        Uri? latencyTarget)
    {
        if (latencyTarget is null)
        {
            return null;
        }

        if (proxy.Extra.TryGetValue(
            latencyTarget.OriginalString,
            out ProxyIndependentHistory? original))
        {
            return original;
        }

        return proxy.Extra.TryGetValue(
            latencyTarget.AbsoluteUri,
            out ProxyIndependentHistory? absolute)
            ? absolute
            : null;
    }

    public static ProxyProviderDisplayItem ProxyProvider(
        ClashProxyProvider provider,
        bool canUpdate,
        bool canCheck)
    {
        ProxySubscriptionInfo? subscription = provider.Subscription;
        long? used = subscription?.Download.HasValue == true || subscription?.Upload.HasValue == true
            ? Math.Max(0, subscription?.Download ?? 0) + Math.Max(0, subscription?.Upload ?? 0)
            : null;
        string total = subscription?.Total is long totalBytes
            ? FormatBytes(totalBytes)
            : "--";
        string usage = used.HasValue || subscription?.Total.HasValue == true
            ? $"{(used.HasValue ? FormatBytes(used.Value) : "--")} / {total}"
            : "--";

        return new ProxyProviderDisplayItem(
            provider.Name,
            EmptyFallback(provider.VehicleType),
            provider.Proxies.Count,
            usage,
            FormatUnixTime(subscription?.Expire),
            FormatDate(provider.UpdatedAt),
            canUpdate,
            canCheck);
    }

    public static ConnectionDisplayItem Connection(
        ClashConnection connection,
        bool canBlock = false,
        ConnectionTransferRate? transferRate = null) => new(
        connection.Id,
        EmptyFallback(ConnectionNormalizer.GetHost(connection)),
        EmptyFallback(connection.Metadata.Network),
        EmptyFallback(ConnectionNormalizer.GetRuleDisplayName(connection)),
        connection.Chains.Count == 0 ? "--" : string.Join(" -> ", connection.Chains),
        transferRate.HasValue ? FormatBytes(transferRate.Value.Download, perSecond: true) : "--",
        transferRate.HasValue ? FormatBytes(transferRate.Value.Upload, perSecond: true) : "--",
        FormatBytes(connection.Download),
        FormatBytes(connection.Upload),
        FormatStartedAt(connection),
        canBlock);

    public static RecentConnectionDisplayItem RecentConnection(
        ClashConnection connection,
        ConnectionTransferRate? transferRate = null)
    {
        ConnectionDisplayItem item = Connection(connection, transferRate: transferRate);
        return new RecentConnectionDisplayItem(
            item.Id,
            item.Host,
            item.Network,
            item.Rule,
            item.Download,
            item.Upload);
    }

    public static RuleDisplayItem Rule(
        ClashRule rule,
        int fallbackIndex,
        bool canDisableByIndex,
        bool canDisableByIdentifier)
    {
        bool disabled = rule.Disabled ?? rule.Statistics?.Disabled ?? false;
        return new RuleDisplayItem(
            rule.Index ?? fallbackIndex,
            rule.Index,
            EmptyFallback(rule.Type),
            EmptyFallback(rule.Payload),
            EmptyFallback(rule.Proxy),
            GetRuleProvider(rule),
            EmptyFallback(rule.Identifier),
            disabled,
            disabled ? "Disabled" : "Enabled",
            (rule.Statistics?.HitCount ?? 0).ToString(CultureInfo.CurrentCulture),
            (rule.Statistics?.MissCount ?? 0).ToString(CultureInfo.CurrentCulture),
            (rule.Index.HasValue && canDisableByIndex) ||
                (!string.IsNullOrWhiteSpace(rule.Identifier) && canDisableByIdentifier));
    }

    public static RuleProviderDisplayItem RuleProvider(
        ClashRuleProvider provider,
        bool canUpdate) => new(
        provider.Name,
        EmptyFallback(provider.Type),
        EmptyFallback(provider.Behavior),
        EmptyFallback(provider.Format),
        provider.RuleCount.ToString("N0", CultureInfo.CurrentCulture),
        FormatDate(provider.UpdatedAt),
        canUpdate);

    public static DnsAnswerDisplayItem DnsAnswer(DnsAnswer answer) => new(
        EmptyFallback(answer.Name),
        answer.Type.ToString(CultureInfo.CurrentCulture),
        EmptyFallback(answer.Data),
        $"{answer.Ttl.ToString(CultureInfo.CurrentCulture)} s");

    public static LogDisplayItem Log(SessionLogEntry entry) => new(
        entry.Sequence,
        entry.Timestamp,
        entry.Level == ClashLogLevel.Unknown
            ? EmptyFallback(entry.RawLevel)
            : entry.Level.ToString(),
        entry.Message);

    public static string FormatBytes(long bytes, bool perSecond = false)
    {
        double value = Math.Max(0, bytes);
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        int unitIndex = 0;
        while (value >= 1024d && unitIndex < units.Length - 1)
        {
            value /= 1024d;
            unitIndex++;
        }

        string number = unitIndex == 0
            ? value.ToString("0", CultureInfo.CurrentCulture)
            : value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.CurrentCulture);
        return perSecond
            ? $"{number} {units[unitIndex]}/s"
            : $"{number} {units[unitIndex]}";
    }

    public static string FormatState(BackendConnectionState state) => state switch
    {
        BackendConnectionState.NoBackend => "No backend",
        BackendConnectionState.Connecting => "Connecting",
        BackendConnectionState.Online => "Online",
        BackendConnectionState.Degraded => "Degraded",
        BackendConnectionState.Unauthorized => "Unauthorized",
        BackendConnectionState.OfflineRetrying => "Retrying",
        _ => "Unknown",
    };

    private static string FormatStartedAt(ClashConnection connection)
    {
        if (connection.StartedAt is DateTimeOffset startedAt)
        {
            return startedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        }

        return EmptyFallback(connection.StartValue);
    }

    private static string FormatDate(DateTimeOffset? value) => value.HasValue
        ? value.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
        : "--";

    private static string FormatUnixTime(long? seconds)
    {
        if (!seconds.HasValue || seconds.Value <= 0)
        {
            return "--";
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds.Value)
                .ToLocalTime()
                .ToString("g", CultureInfo.CurrentCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return "--";
        }
    }

    private static string GetRuleProvider(ClashRule rule) =>
        rule.Type.Contains("RULE-SET", StringComparison.OrdinalIgnoreCase) ||
        rule.Type.Contains("RULESET", StringComparison.OrdinalIgnoreCase)
            ? EmptyFallback(rule.Payload)
            : "--";

    private static string EmptyFallback(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "--" : value;
}
