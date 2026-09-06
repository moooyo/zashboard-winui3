using System.Text.Json;
using System.Text.Json.Serialization;
using Zashboard.Infrastructure.Clash.Serialization;

namespace Zashboard.Infrastructure.Clash.Wire;

internal sealed class VersionResponseDto : IJsonOnDeserialized
{
    [JsonPropertyName("version")]
    [JsonRequired]
    public string? Version { get; set; }

    void IJsonOnDeserialized.OnDeserialized() =>
        WireContract.RequireNotBlank(Version, "version");
}

internal sealed class ProxyCatalogDto : IJsonOnDeserialized
{
    [JsonPropertyName("proxies")]
    [JsonRequired]
    public Dictionary<string, ProxyDto>? Proxies { get; set; }

    void IJsonOnDeserialized.OnDeserialized() =>
        WireContract.RequireDictionaryValuesNotNull(Proxies, "proxies");
}

internal sealed class ProxyDto : IJsonOnDeserialized
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("history")]
    public List<ProxyHistoryEntryDto>? History { get; set; }

    [JsonPropertyName("extra")]
    public Dictionary<string, ProxyIndependentHistoryDto>? Extra { get; set; }

    [JsonPropertyName("all")]
    public List<string>? All { get; set; }

    [JsonPropertyName("alive")]
    public bool? Alive { get; set; }

    [JsonPropertyName("udp")]
    public bool? Udp { get; set; }

    [JsonPropertyName("xudp")]
    public bool? Xudp { get; set; }

    [JsonPropertyName("now")]
    public string? Now { get; set; }

    [JsonPropertyName("fixed")]
    public string? Fixed { get; set; }

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    [JsonPropertyName("hidden")]
    public bool? Hidden { get; set; }

    [JsonPropertyName("selectable")]
    public bool? Selectable { get; set; }

    [JsonPropertyName("testUrl")]
    public string? TestUrl { get; set; }

    [JsonPropertyName("dialer-proxy")]
    public string? DialerProxy { get; set; }

    [JsonPropertyName("provider-name")]
    public string? ProviderName { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }

    void IJsonOnDeserialized.OnDeserialized() =>
        WireContract.RequireNotBlank(Type, "type");
}

internal sealed class ProxyHistoryEntryDto
{
    [JsonPropertyName("time")]
    public string? Time { get; set; }

    [JsonPropertyName("delay")]
    public int Delay { get; set; }
}

internal sealed class ProxyIndependentHistoryDto
{
    [JsonPropertyName("alive")]
    public bool Alive { get; set; }

    [JsonPropertyName("history")]
    public List<ProxyHistoryEntryDto>? History { get; set; }
}

internal sealed class ProxyProviderCatalogDto : IJsonOnDeserialized
{
    [JsonPropertyName("providers")]
    [JsonRequired]
    public Dictionary<string, ProxyProviderDto>? Providers { get; set; }

    void IJsonOnDeserialized.OnDeserialized() =>
        WireContract.RequireDictionaryValuesNotNull(Providers, "providers");
}

internal sealed class ProxyProviderDto : IJsonOnDeserialized
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("proxies")]
    public List<ProxyDto>? Proxies { get; set; }

    [JsonPropertyName("testUrl")]
    public string? TestUrl { get; set; }

    [JsonPropertyName("updatedAt")]
    public string? UpdatedAt { get; set; }

    [JsonPropertyName("vehicleType")]
    public string? VehicleType { get; set; }

    [JsonPropertyName("subscriptionInfo")]
    public ProxySubscriptionInfoDto? SubscriptionInfo { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }

    void IJsonOnDeserialized.OnDeserialized()
    {
        WireContract.RequireItemsNotNull(Proxies, "proxies");
        WireContract.RequireNotBlank(VehicleType, "vehicleType");
    }
}

internal sealed class ProxySubscriptionInfoDto
{
    [JsonPropertyName("Download")]
    public long? Download { get; set; }

    [JsonPropertyName("Upload")]
    public long? Upload { get; set; }

    [JsonPropertyName("Total")]
    public long? Total { get; set; }

    [JsonPropertyName("Expire")]
    public long? Expire { get; set; }
}

internal sealed class DelayResponseDto
{
    [JsonPropertyName("delay")]
    [JsonRequired]
    public int Delay { get; set; }
}

internal sealed class ProxySelectionRequestDto
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }
}

internal sealed class RuleCatalogDto : IJsonOnDeserialized
{
    [JsonPropertyName("rules")]
    [JsonRequired]
    public List<RuleDto>? Rules { get; set; }

    void IJsonOnDeserialized.OnDeserialized() =>
        WireContract.RequireItemsNotNull(Rules, "rules");
}

internal sealed class RuleDto : IJsonOnDeserialized
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("payload")]
    public string? Payload { get; set; }

    [JsonPropertyName("proxy")]
    public string? Proxy { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("uuid")]
    public string? Identifier { get; set; }

    [JsonPropertyName("disabled")]
    public bool? Disabled { get; set; }

    [JsonPropertyName("index")]
    public int? Index { get; set; }

    [JsonPropertyName("extra")]
    public RuleStatisticsDto? Statistics { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }

    void IJsonOnDeserialized.OnDeserialized()
    {
        WireContract.RequireNotBlank(Type, "type");
        WireContract.RequireNotNull(Payload, "payload");
        WireContract.RequireNotBlank(Proxy, "proxy");
    }
}

internal sealed class RuleStatisticsDto
{
    [JsonPropertyName("disabled")]
    public bool Disabled { get; set; }

    [JsonPropertyName("hitAt")]
    public string? HitAt { get; set; }

    [JsonPropertyName("hitCount")]
    public long HitCount { get; set; }

    [JsonPropertyName("missAt")]
    public string? MissAt { get; set; }

    [JsonPropertyName("missCount")]
    public long MissCount { get; set; }
}

internal sealed class RuleProviderCatalogDto : IJsonOnDeserialized
{
    [JsonPropertyName("providers")]
    [JsonRequired]
    public Dictionary<string, RuleProviderDto>? Providers { get; set; }

    void IJsonOnDeserialized.OnDeserialized() =>
        WireContract.RequireDictionaryValuesNotNull(Providers, "providers");
}

internal sealed class RuleProviderDto : IJsonOnDeserialized
{
    [JsonPropertyName("behavior")]
    public string? Behavior { get; set; }

    [JsonPropertyName("format")]
    public string? Format { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("ruleCount")]
    public long RuleCount { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("updatedAt")]
    public string? UpdatedAt { get; set; }

    [JsonPropertyName("vehicleType")]
    public string? VehicleType { get; set; }

    void IJsonOnDeserialized.OnDeserialized()
    {
        WireContract.RequireNotBlank(Type, "type");
        WireContract.RequireNotBlank(VehicleType, "vehicleType");
    }
}

internal sealed class ConfigurationDto : IJsonOnDeserialized
{
    [JsonPropertyName("port")]
    [JsonRequired]
    public int Port { get; set; }

    [JsonPropertyName("socks-port")]
    [JsonRequired]
    public int SocksPort { get; set; }

    [JsonPropertyName("redir-port")]
    [JsonRequired]
    public int RedirPort { get; set; }

    [JsonPropertyName("tproxy-port")]
    [JsonRequired]
    public int TProxyPort { get; set; }

    [JsonPropertyName("mixed-port")]
    [JsonRequired]
    public int MixedPort { get; set; }

    [JsonPropertyName("allow-lan")]
    [JsonRequired]
    public bool AllowLan { get; set; }

    [JsonPropertyName("bind-address")]
    public string? BindAddress { get; set; }

    [JsonPropertyName("mode")]
    [JsonRequired]
    public string? Mode { get; set; }

    [JsonPropertyName("mode-list")]
    public List<string>? ModeList { get; set; }

    [JsonPropertyName("modes")]
    public List<string>? Modes { get; set; }

    [JsonPropertyName("log-level")]
    public string? LogLevel { get; set; }

    [JsonPropertyName("ipv6")]
    public bool Ipv6 { get; set; }

    [JsonPropertyName("tun")]
    public TunConfigurationDto? Tun { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }

    void IJsonOnDeserialized.OnDeserialized() =>
        WireContract.RequireNotBlank(Mode, "mode");
}

internal sealed class TunConfigurationDto
{
    [JsonPropertyName("enable")]
    public bool Enabled { get; set; }
}

internal sealed class ConfigurationPatchDto
{
    [JsonPropertyName("port")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Port { get; set; }

    [JsonPropertyName("socks-port")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SocksPort { get; set; }

    [JsonPropertyName("redir-port")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RedirPort { get; set; }

    [JsonPropertyName("tproxy-port")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TProxyPort { get; set; }

    [JsonPropertyName("mixed-port")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MixedPort { get; set; }

    [JsonPropertyName("allow-lan")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AllowLan { get; set; }

    [JsonPropertyName("bind-address")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BindAddress { get; set; }

    [JsonPropertyName("mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mode { get; set; }

    [JsonPropertyName("log-level")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LogLevel { get; set; }

    [JsonPropertyName("ipv6")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Ipv6 { get; set; }

    [JsonPropertyName("tun")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TunConfigurationPatchDto? Tun { get; set; }
}

internal sealed class TunConfigurationPatchDto
{
    [JsonPropertyName("enable")]
    public bool Enabled { get; set; }
}

internal sealed class ConfigurationUpdateDto
{
    [JsonPropertyName("path")]
    public required string Path { get; set; }

    [JsonPropertyName("payload")]
    public required string Payload { get; set; }
}

internal sealed class DnsQueryResponseDto
{
    [JsonPropertyName("AD")]
    public bool AuthenticatedData { get; set; }

    [JsonPropertyName("CD")]
    public bool CheckingDisabled { get; set; }

    [JsonPropertyName("RA")]
    public bool RecursionAvailable { get; set; }

    [JsonPropertyName("RD")]
    public bool RecursionDesired { get; set; }

    [JsonPropertyName("TC")]
    public bool Truncated { get; set; }

    [JsonPropertyName("status")]
    [JsonRequired]
    public int Status { get; set; }

    [JsonPropertyName("Question")]
    public List<DnsQuestionDto>? Questions { get; set; }

    [JsonPropertyName("Answer")]
    public List<DnsAnswerDto>? Answers { get; set; }
}

internal sealed class DnsQuestionDto
{
    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("Qtype")]
    public int Type { get; set; }

    [JsonPropertyName("Qclass")]
    public int Class { get; set; }
}

internal sealed class DnsAnswerDto
{
    [JsonPropertyName("TTL")]
    public int Ttl { get; set; }

    [JsonPropertyName("data")]
    public string? Data { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("type")]
    public int Type { get; set; }
}

internal sealed class SmartWeightsDto : IJsonOnDeserialized
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("weights")]
    [JsonRequired]
    public Dictionary<string, List<SmartNodeRankDto>>? Weights { get; set; }

    void IJsonOnDeserialized.OnDeserialized()
    {
        WireContract.RequireDictionaryValuesNotNull(Weights, "weights");
        foreach ((string groupName, List<SmartNodeRankDto> ranks) in Weights!)
        {
            WireContract.RequireItemsNotNull(ranks, $"weights.{groupName}");
        }
    }
}

internal sealed class SmartNodeRankDto : IJsonOnDeserialized
{
    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("Rank")]
    public string? Rank { get; set; }

    [JsonPropertyName("Weight")]
    [JsonRequired]
    public double Weight { get; set; }

    void IJsonOnDeserialized.OnDeserialized()
    {
        WireContract.RequireNotBlank(Name, nameof(Name));
        WireContract.RequireNotBlank(Rank, nameof(Rank));
    }
}

internal sealed class HonkRuntimeStatisticsDto : IJsonOnDeserialized
{
    [JsonPropertyName("outbounds")]
    [JsonRequired]
    public List<HonkOutboundStatisticsDto>? Outbounds { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }

    void IJsonOnDeserialized.OnDeserialized() =>
        WireContract.RequireItemsNotNull(Outbounds, "outbounds");
}

internal sealed class HonkOutboundStatisticsDto : IJsonOnDeserialized
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("totalConns")]
    [JsonRequired]
    public long TotalConnections { get; set; }

    [JsonPropertyName("activeConns")]
    [JsonRequired]
    public long ActiveConnections { get; set; }

    [JsonPropertyName("upload")]
    [JsonRequired]
    public long Upload { get; set; }

    [JsonPropertyName("download")]
    [JsonRequired]
    public long Download { get; set; }

    [JsonPropertyName("errors")]
    [JsonRequired]
    public long Errors { get; set; }

    void IJsonOnDeserialized.OnDeserialized() =>
        WireContract.RequireNotBlank(Name, "name");
}

internal sealed class ConnectionStreamSnapshotDto : IJsonOnDeserialized
{
    [JsonPropertyName("connections")]
    [JsonRequired]
    public List<ConnectionDto>? Connections { get; set; }

    [JsonPropertyName("downloadTotal")]
    [JsonRequired]
    public long DownloadTotal { get; set; }

    [JsonPropertyName("uploadTotal")]
    [JsonRequired]
    public long UploadTotal { get; set; }

    [JsonPropertyName("memory")]
    public long Memory { get; set; }

    void IJsonOnDeserialized.OnDeserialized()
    {
        // Mihomo serializes an uninitialized connection slice as null when no connections exist.
        if (Connections is not null)
        {
            WireContract.RequireItemsNotNull(Connections, "connections");
        }
    }
}

internal sealed class ConnectionDto : IJsonOnDeserialized
{
    [JsonPropertyName("id")]
    [JsonRequired]
    public string? Id { get; set; }

    [JsonPropertyName("download")]
    [JsonRequired]
    public long Download { get; set; }

    [JsonPropertyName("upload")]
    [JsonRequired]
    public long Upload { get; set; }

    [JsonPropertyName("chains")]
    public List<string>? Chains { get; set; }

    [JsonPropertyName("rule")]
    public string? Rule { get; set; }

    [JsonPropertyName("rulePayload")]
    public string? RulePayload { get; set; }

    [JsonPropertyName("start")]
    public JsonElement Start { get; set; }

    [JsonPropertyName("metadata")]
    [JsonRequired]
    public ConnectionMetadataDto? Metadata { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }

    void IJsonOnDeserialized.OnDeserialized()
    {
        WireContract.RequireNotBlank(Id, "id");
        WireContract.RequireNotNull(Metadata, "metadata");
    }
}

internal sealed class ConnectionMetadataDto
{
    [JsonPropertyName("destinationGeoIP")]
    [JsonConverter(typeof(StringOrStringArrayJsonConverter))]
    public List<string>? DestinationGeoIp { get; set; }

    [JsonPropertyName("destinationIP")]
    public string? DestinationIp { get; set; }

    [JsonPropertyName("destinationIPASN")]
    public string? DestinationIpAsn { get; set; }

    [JsonPropertyName("destinationPort")]
    public string? DestinationPort { get; set; }

    [JsonPropertyName("dnsMode")]
    public string? DnsMode { get; set; }

    [JsonPropertyName("dscp")]
    public int Dscp { get; set; }

    [JsonPropertyName("host")]
    public string? Host { get; set; }

    [JsonPropertyName("inboundIP")]
    public string? InboundIp { get; set; }

    [JsonPropertyName("inboundName")]
    public string? InboundName { get; set; }

    [JsonPropertyName("inboundPort")]
    public string? InboundPort { get; set; }

    [JsonPropertyName("inboundUser")]
    public string? InboundUser { get; set; }

    [JsonPropertyName("network")]
    public string? Network { get; set; }

    [JsonPropertyName("process")]
    public string? Process { get; set; }

    [JsonPropertyName("processPath")]
    public string? ProcessPath { get; set; }

    [JsonPropertyName("remoteDestination")]
    public string? RemoteDestination { get; set; }

    [JsonPropertyName("sniffHost")]
    public string? SniffHost { get; set; }

    [JsonPropertyName("sourceGeoIP")]
    [JsonConverter(typeof(StringOrStringArrayJsonConverter))]
    public List<string>? SourceGeoIp { get; set; }

    [JsonPropertyName("sourceIP")]
    public string? SourceIp { get; set; }

    [JsonPropertyName("sourceIPASN")]
    public string? SourceIpAsn { get; set; }

    [JsonPropertyName("sourcePort")]
    public string? SourcePort { get; set; }

    [JsonPropertyName("specialProxy")]
    public string? SpecialProxy { get; set; }

    [JsonPropertyName("specialRules")]
    public string? SpecialRules { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("uid")]
    public long Uid { get; set; }

    [JsonPropertyName("smartBlock")]
    public string? SmartBlock { get; set; }
}

internal sealed class LogMessageDto : IJsonOnDeserialized
{
    [JsonPropertyName("type")]
    [JsonRequired]
    public string? Type { get; set; }

    [JsonPropertyName("payload")]
    [JsonRequired]
    public string? Payload { get; set; }

    void IJsonOnDeserialized.OnDeserialized()
    {
        WireContract.RequireNotBlank(Type, "type");
        WireContract.RequireNotNull(Payload, "payload");
    }
}

internal sealed class TrafficSampleDto
{
    [JsonPropertyName("down")]
    [JsonRequired]
    public long Down { get; set; }

    [JsonPropertyName("up")]
    [JsonRequired]
    public long Up { get; set; }

    [JsonPropertyName("downTotal")]
    public long? DownTotal { get; set; }

    [JsonPropertyName("upTotal")]
    public long? UpTotal { get; set; }
}

internal sealed class MemorySampleDto
{
    [JsonPropertyName("inuse")]
    [JsonRequired]
    public long InUse { get; set; }
}

internal static class WireContract
{
    public static void RequireNotNull(object? value, string propertyName)
    {
        if (value is null)
        {
            throw new JsonException($"The required Clash field '{propertyName}' cannot be null.");
        }
    }

    public static void RequireNotBlank(string? value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new JsonException($"The required Clash field '{propertyName}' cannot be blank.");
        }
    }

    public static void RequireItemsNotNull<T>(
        IEnumerable<T>? values,
        string propertyName)
        where T : class
    {
        RequireNotNull(values, propertyName);
        if (values!.Any(static value => value is null))
        {
            throw new JsonException(
                $"The required Clash collection '{propertyName}' cannot contain null items.");
        }
    }

    public static void RequireDictionaryValuesNotNull<T>(
        IReadOnlyDictionary<string, T>? values,
        string propertyName)
        where T : class
    {
        RequireNotNull(values, propertyName);
        if (values!.Values.Any(static value => value is null))
        {
            throw new JsonException(
                $"The required Clash object '{propertyName}' cannot contain null values.");
        }
    }
}
