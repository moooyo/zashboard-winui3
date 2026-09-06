using System.Globalization;
using System.Text.Json;
using Zashboard.Core.Clash;
using Zashboard.Core.Normalization;
using Zashboard.Infrastructure.Clash.Wire;

namespace Zashboard.Infrastructure.Clash.Serialization;

internal static class ClashWireMapper
{
    public static ClashVersion ToDomain(VersionResponseDto value)
    {
        string version = value.Version ?? string.Empty;
        return new ClashVersion
        {
            Value = version,
            CoreKind = ClashTypeNormalizer.ClassifyCore(version),
        };
    }

    public static ProxyCatalog ToDomain(ProxyCatalogDto value)
    {
        Dictionary<string, ClashProxy> proxies = new(StringComparer.Ordinal);
        if (value.Proxies is not null)
        {
            foreach ((string name, ProxyDto proxy) in value.Proxies)
            {
                if (proxy is not null)
                {
                    proxies[name] = ToDomain(proxy, name);
                }
            }
        }

        return new ProxyCatalog { Proxies = proxies };
    }

    public static ProxyProviderCatalog ToDomain(ProxyProviderCatalogDto value)
    {
        Dictionary<string, ClashProxyProvider> providers = new(StringComparer.Ordinal);
        if (value.Providers is not null)
        {
            foreach ((string name, ProxyProviderDto provider) in value.Providers)
            {
                if (provider is not null)
                {
                    providers[name] = ToDomain(provider, name);
                }
            }
        }

        return new ProxyProviderCatalog { Providers = providers };
    }

    public static RuleCatalog ToDomain(RuleCatalogDto value) => new()
    {
        Rules = value.Rules is null
            ? []
            : value.Rules.Where(static item => item is not null).Select(ToDomain).ToArray(),
    };

    public static RuleProviderCatalog ToDomain(RuleProviderCatalogDto value)
    {
        Dictionary<string, ClashRuleProvider> providers = new(StringComparer.Ordinal);
        if (value.Providers is not null)
        {
            foreach ((string name, RuleProviderDto provider) in value.Providers)
            {
                if (provider is not null)
                {
                    providers[name] = new ClashRuleProvider
                    {
                        Behavior = provider.Behavior ?? string.Empty,
                        Format = provider.Format ?? string.Empty,
                        Name = string.IsNullOrWhiteSpace(provider.Name) ? name : provider.Name,
                        RuleCount = provider.RuleCount,
                        Type = provider.Type ?? string.Empty,
                        UpdatedAt = ParseTimestamp(provider.UpdatedAt),
                        VehicleType = provider.VehicleType ?? string.Empty,
                    };
                }
            }
        }

        return new RuleProviderCatalog { Providers = providers };
    }

    public static ClashConfiguration ToDomain(ConfigurationDto value) => new()
    {
        Port = value.Port,
        SocksPort = value.SocksPort,
        RedirPort = value.RedirPort,
        TProxyPort = value.TProxyPort,
        MixedPort = value.MixedPort,
        AllowLan = value.AllowLan,
        BindAddress = value.BindAddress ?? string.Empty,
        Mode = value.Mode ?? string.Empty,
        ModeList = CleanStrings(value.ModeList),
        Modes = CleanStrings(value.Modes),
        LogLevel = value.LogLevel ?? string.Empty,
        Ipv6 = value.Ipv6,
        Tun = value.Tun is null
            ? null
            : new ClashTunConfiguration { Enabled = value.Tun.Enabled },
    };

    public static DnsQueryResult ToDomain(DnsQueryResponseDto value) => new()
    {
        AuthenticatedData = value.AuthenticatedData,
        CheckingDisabled = value.CheckingDisabled,
        RecursionAvailable = value.RecursionAvailable,
        RecursionDesired = value.RecursionDesired,
        Truncated = value.Truncated,
        Status = value.Status,
        Questions = value.Questions is null
            ? []
            : value.Questions.Where(static item => item is not null).Select(static item => new DnsQuestion
            {
                Name = item.Name ?? string.Empty,
                Type = item.Type,
                Class = item.Class,
            }).ToArray(),
        Answers = value.Answers is null
            ? []
            : value.Answers.Where(static item => item is not null).Select(static item => new DnsAnswer
            {
                Ttl = item.Ttl,
                Data = item.Data ?? string.Empty,
                Name = item.Name ?? string.Empty,
                Type = item.Type,
            }).ToArray(),
    };

    public static SmartWeights ToDomain(SmartWeightsDto value)
    {
        Dictionary<string, IReadOnlyList<SmartNodeRank>> weights = new(StringComparer.Ordinal);
        if (value.Weights is not null)
        {
            foreach ((string name, List<SmartNodeRankDto> ranks) in value.Weights)
            {
                weights[name] = ranks is null
                    ? []
                    : ranks.Where(static item => item is not null).Select(static item => new SmartNodeRank
                    {
                        Name = item.Name ?? string.Empty,
                        Rank = item.Rank ?? string.Empty,
                        Weight = item.Weight,
                    }).ToArray();
            }
        }

        return new SmartWeights
        {
            Message = value.Message ?? string.Empty,
            Weights = weights,
        };
    }

    public static HonkRuntimeStatistics ToDomain(HonkRuntimeStatisticsDto value) => new()
    {
        Outbounds = value.Outbounds is null
            ? []
            : value.Outbounds.Where(static item => item is not null).Select(static item => new HonkOutboundStatistics
            {
                Name = item.Name ?? string.Empty,
                TotalConnections = item.TotalConnections,
                ActiveConnections = item.ActiveConnections,
                Upload = item.Upload,
                Download = item.Download,
                Errors = item.Errors,
            }).ToArray(),
    };

    public static DashboardStorage ToDomain(Dictionary<string, JsonElement> value)
    {
        Dictionary<string, string> values = new(value.Count, StringComparer.Ordinal);
        foreach ((string key, JsonElement item) in value)
        {
            values[key] = item.ValueKind switch
            {
                JsonValueKind.String => item.GetString() ?? string.Empty,
                JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                _ => item.GetRawText(),
            };
        }

        return new DashboardStorage { Values = values };
    }

    public static ConnectionStreamSnapshot ToDomain(ConnectionStreamSnapshotDto value) => new()
    {
        Connections = value.Connections is null
            ? []
            : value.Connections.Where(static item => item is not null).Select(ToDomain).ToArray(),
        DownloadTotal = value.DownloadTotal,
        UploadTotal = value.UploadTotal,
        Memory = value.Memory,
    };

    public static ClashLogMessage ToDomain(LogMessageDto value)
    {
        string level = value.Type ?? string.Empty;
        return new ClashLogMessage
        {
            Level = ClashTypeNormalizer.NormalizeLogLevel(level),
            RawLevel = level,
            Payload = value.Payload ?? string.Empty,
        };
    }

    public static ClashTrafficSample ToDomain(TrafficSampleDto value) => new()
    {
        Down = value.Down,
        Up = value.Up,
        DownTotal = value.DownTotal,
        UpTotal = value.UpTotal,
    };

    public static ClashMemorySample ToDomain(MemorySampleDto value) => new()
    {
        InUse = value.InUse,
    };

    public static ConfigurationPatchDto ToWire(ClashConfigurationPatch value) => new()
    {
        Port = value.Port,
        SocksPort = value.SocksPort,
        RedirPort = value.RedirPort,
        TProxyPort = value.TProxyPort,
        MixedPort = value.MixedPort,
        AllowLan = value.AllowLan,
        BindAddress = value.BindAddress,
        Mode = value.Mode,
        LogLevel = value.LogLevel,
        Ipv6 = value.Ipv6,
        Tun = value.TunEnabled.HasValue
            ? new TunConfigurationPatchDto { Enabled = value.TunEnabled.Value }
            : null,
    };

    public static ConfigurationUpdateDto ToWire(ClashConfigurationUpdate value) => new()
    {
        Path = value.Path,
        Payload = value.Payload,
    };

    private static ClashProxy ToDomain(ProxyDto value, string fallbackName)
    {
        Dictionary<string, ProxyIndependentHistory> extra = new(StringComparer.Ordinal);
        if (value.Extra is not null)
        {
            foreach ((string url, ProxyIndependentHistoryDto history) in value.Extra)
            {
                if (history is not null)
                {
                    extra[url] = new ProxyIndependentHistory
                    {
                        Alive = history.Alive,
                        History = MapHistory(history.History),
                    };
                }
            }
        }

        string type = value.Type ?? string.Empty;
        return new ClashProxy
        {
            Name = string.IsNullOrWhiteSpace(value.Name) ? fallbackName : value.Name,
            Type = type,
            Kind = ClashTypeNormalizer.NormalizeProxyKind(type),
            History = MapHistory(value.History),
            Extra = extra,
            All = CleanStrings(value.All),
            Alive = value.Alive,
            Udp = value.Udp,
            Xudp = value.Xudp,
            Now = value.Now,
            Fixed = value.Fixed,
            Icon = value.Icon,
            Hidden = value.Hidden,
            Selectable = value.Selectable,
            TestUrl = ParseAbsoluteUri(value.TestUrl),
            DialerProxy = value.DialerProxy,
            ProviderName = value.ProviderName,
        };
    }

    private static ClashProxyProvider ToDomain(ProxyProviderDto value, string fallbackName) => new()
    {
        Name = string.IsNullOrWhiteSpace(value.Name) ? fallbackName : value.Name,
        Proxies = value.Proxies is null
            ? []
            : value.Proxies.Where(static item => item is not null).Select(item => ToDomain(item, item.Name ?? string.Empty)).ToArray(),
        TestUrl = ParseAbsoluteUri(value.TestUrl),
        UpdatedAt = ParseTimestamp(value.UpdatedAt),
        VehicleType = value.VehicleType ?? string.Empty,
        Subscription = value.SubscriptionInfo is null
            ? null
            : new ProxySubscriptionInfo
            {
                Download = value.SubscriptionInfo.Download,
                Upload = value.SubscriptionInfo.Upload,
                Total = value.SubscriptionInfo.Total,
                Expire = value.SubscriptionInfo.Expire,
            },
    };

    private static ClashRule ToDomain(RuleDto value) => new()
    {
        Type = value.Type ?? string.Empty,
        Payload = value.Payload ?? string.Empty,
        Proxy = value.Proxy ?? string.Empty,
        Size = value.Size,
        Identifier = string.IsNullOrWhiteSpace(value.Identifier) ? null : value.Identifier,
        Disabled = value.Disabled,
        Index = value.Index,
        Statistics = value.Statistics is null
            ? null
            : new ClashRuleStatistics
            {
                Disabled = value.Statistics.Disabled,
                HitAt = ParseTimestamp(value.Statistics.HitAt),
                HitCount = value.Statistics.HitCount,
                MissAt = ParseTimestamp(value.Statistics.MissAt),
                MissCount = value.Statistics.MissCount,
            },
    };

    private static ClashConnection ToDomain(ConnectionDto value)
    {
        ConnectionMetadataDto metadata = value.Metadata ?? new ConnectionMetadataDto();
        string startValue = GetStartValue(value.Start);

        return new ClashConnection
        {
            Id = value.Id ?? string.Empty,
            Download = value.Download,
            Upload = value.Upload,
            Chains = CleanStrings(value.Chains),
            Rule = value.Rule ?? string.Empty,
            RulePayload = value.RulePayload ?? string.Empty,
            StartedAt = ParseStart(value.Start),
            StartValue = startValue,
            Metadata = new ClashConnectionMetadata
            {
                DestinationGeoIp = CleanStrings(metadata.DestinationGeoIp),
                DestinationIp = metadata.DestinationIp ?? string.Empty,
                DestinationIpAsn = metadata.DestinationIpAsn ?? string.Empty,
                DestinationPort = metadata.DestinationPort ?? string.Empty,
                DnsMode = metadata.DnsMode ?? string.Empty,
                Dscp = metadata.Dscp,
                Host = metadata.Host ?? string.Empty,
                InboundIp = metadata.InboundIp ?? string.Empty,
                InboundName = metadata.InboundName ?? string.Empty,
                InboundPort = metadata.InboundPort ?? string.Empty,
                InboundUser = metadata.InboundUser ?? string.Empty,
                Network = metadata.Network ?? string.Empty,
                Process = metadata.Process ?? string.Empty,
                ProcessPath = metadata.ProcessPath ?? string.Empty,
                RemoteDestination = metadata.RemoteDestination ?? string.Empty,
                SniffHost = metadata.SniffHost ?? string.Empty,
                SourceGeoIp = CleanStrings(metadata.SourceGeoIp),
                SourceIp = metadata.SourceIp ?? string.Empty,
                SourceIpAsn = metadata.SourceIpAsn ?? string.Empty,
                SourcePort = metadata.SourcePort ?? string.Empty,
                SpecialProxy = metadata.SpecialProxy ?? string.Empty,
                SpecialRules = metadata.SpecialRules ?? string.Empty,
                Type = metadata.Type ?? string.Empty,
                Uid = metadata.Uid,
                SmartBlock = metadata.SmartBlock ?? string.Empty,
            },
        };
    }

    private static ProxyHistoryEntry[] MapHistory(List<ProxyHistoryEntryDto>? value) =>
        value is null
            ? []
            : value.Where(static item => item is not null).Select(static item => new ProxyHistoryEntry
            {
                Time = item.Time ?? string.Empty,
                Delay = item.Delay,
            }).ToArray();

    private static string[] CleanStrings(List<string>? value) =>
        value is null
            ? []
            : value.Where(static item => !string.IsNullOrEmpty(item)).ToArray();

    private static Uri? ParseAbsoluteUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ? uri : null;

    private static DateTimeOffset? ParseTimestamp(string? value) =>
        ClashTypeNormalizer.TryNormalizeTimestamp(value, out DateTimeOffset timestamp)
            ? timestamp
            : null;

    private static string GetStartValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.GetRawText(),
        _ => string.Empty,
    };

    private static DateTimeOffset? ParseStart(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return ParseTimestamp(value.GetString());
        }

        if (value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out long timestamp) &&
            ClashTypeNormalizer.TryNormalizeUnixTimestamp(timestamp, out DateTimeOffset parsed))
        {
            return parsed;
        }

        if (value.ValueKind == JsonValueKind.Number &&
            double.TryParse(value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out double fractional))
        {
            double millisecondsValue = fractional >= 100_000_000_000d
                ? fractional
                : fractional * 1000d;
            if (!double.IsFinite(millisecondsValue) ||
                millisecondsValue < long.MinValue ||
                millisecondsValue > long.MaxValue)
            {
                return null;
            }

            long milliseconds = (long)millisecondsValue;
            if (ClashTypeNormalizer.TryNormalizeUnixTimestamp(milliseconds, out parsed))
            {
                return parsed;
            }
        }

        return null;
    }
}
