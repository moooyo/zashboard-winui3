namespace Zashboard.Core.Clash;

public enum ClashProxyKind
{
    Unknown,
    Direct,
    Reject,
    RejectDrop,
    Block,
    Compatible,
    Pass,
    PassRule,
    Rematch,
    Dns,
    Relay,
    Selector,
    Fallback,
    UrlTest,
    Smart,
    LoadBalance,
}

public sealed record ProxyHistoryEntry
{
    public string Time { get; init; } = string.Empty;

    public int Delay { get; init; }
}

public sealed record ProxyIndependentHistory
{
    public bool Alive { get; init; }

    public IReadOnlyList<ProxyHistoryEntry> History { get; init; } = [];
}

public sealed record ClashProxy
{
    public string Name { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public ClashProxyKind Kind { get; init; } = ClashProxyKind.Unknown;

    public IReadOnlyList<ProxyHistoryEntry> History { get; init; } = [];

    public IReadOnlyDictionary<string, ProxyIndependentHistory> Extra { get; init; } =
        new Dictionary<string, ProxyIndependentHistory>(StringComparer.Ordinal);

    public IReadOnlyList<string> All { get; init; } = [];

    public bool? Alive { get; init; }

    public bool? Udp { get; init; }

    public bool? Xudp { get; init; }

    public string? Now { get; init; }

    public string? Fixed { get; init; }

    public string? Icon { get; init; }

    public bool? Hidden { get; init; }

    public bool? Selectable { get; init; }

    public bool AllowsManualSelection => Selectable == true ||
        (Selectable != false && Kind is ClashProxyKind.Selector or ClashProxyKind.Smart);

    public Uri? TestUrl { get; init; }

    public string? DialerProxy { get; init; }

    public string? ProviderName { get; init; }
}

public sealed record ProxyCatalog
{
    public IReadOnlyDictionary<string, ClashProxy> Proxies { get; init; } =
        new Dictionary<string, ClashProxy>(StringComparer.Ordinal);
}

public sealed record ProxySubscriptionInfo
{
    public long? Download { get; init; }

    public long? Upload { get; init; }

    public long? Total { get; init; }

    public long? Expire { get; init; }
}

public sealed record ClashProxyProvider
{
    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<ClashProxy> Proxies { get; init; } = [];

    public Uri? TestUrl { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    public string VehicleType { get; init; } = string.Empty;

    public ProxySubscriptionInfo? Subscription { get; init; }
}

public sealed record ProxyProviderCatalog
{
    public IReadOnlyDictionary<string, ClashProxyProvider> Providers { get; init; } =
        new Dictionary<string, ClashProxyProvider>(StringComparer.Ordinal);
}

public readonly record struct ProxyDelay(int Milliseconds)
{
    public bool IsReachable => Milliseconds > 0;
}

public sealed class ProxyDelayRequest
{
    public ProxyDelayRequest(Uri targetUrl, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(targetUrl);

        if (!targetUrl.IsAbsoluteUri)
        {
            throw new ArgumentException("The latency target must be an absolute URI.", nameof(targetUrl));
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The timeout must be positive.");
        }

        TargetUrl = targetUrl;
        Timeout = timeout;
    }

    public Uri TargetUrl { get; }

    public TimeSpan Timeout { get; }
}
