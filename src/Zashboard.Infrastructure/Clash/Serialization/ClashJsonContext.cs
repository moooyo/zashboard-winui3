using System.Text.Json;
using System.Text.Json.Serialization;
using Zashboard.Infrastructure.Clash.Wire;

namespace Zashboard.Infrastructure.Clash.Serialization;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(VersionResponseDto))]
[JsonSerializable(typeof(ProxyCatalogDto))]
[JsonSerializable(typeof(ProxyProviderCatalogDto))]
[JsonSerializable(typeof(DelayResponseDto))]
[JsonSerializable(typeof(ProxySelectionRequestDto))]
[JsonSerializable(typeof(RuleCatalogDto))]
[JsonSerializable(typeof(RuleProviderCatalogDto))]
[JsonSerializable(typeof(ConfigurationDto))]
[JsonSerializable(typeof(ConfigurationPatchDto))]
[JsonSerializable(typeof(ConfigurationUpdateDto))]
[JsonSerializable(typeof(DnsQueryResponseDto))]
[JsonSerializable(typeof(SmartWeightsDto))]
[JsonSerializable(typeof(HonkRuntimeStatisticsDto))]
[JsonSerializable(typeof(ConnectionStreamSnapshotDto))]
[JsonSerializable(typeof(LogMessageDto))]
[JsonSerializable(typeof(TrafficSampleDto))]
[JsonSerializable(typeof(MemorySampleDto))]
[JsonSerializable(typeof(Dictionary<string, int>))]
[JsonSerializable(typeof(Dictionary<string, bool>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
internal sealed partial class ClashJsonContext : JsonSerializerContext;
