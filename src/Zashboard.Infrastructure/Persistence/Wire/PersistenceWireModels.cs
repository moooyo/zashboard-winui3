using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zashboard.Infrastructure.Persistence.Wire;

internal sealed class BackendProfilesDocumentDto
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("activeProfileId")]
    public Guid? ActiveProfileId { get; set; }

    [JsonPropertyName("profiles")]
    public List<BackendProfileDto>? Profiles { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

internal sealed class BackendProfileDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("endpoint")]
    public string? Endpoint { get; set; }

    [JsonPropertyName("disableCoreUpgrade")]
    public bool DisableCoreUpgrade { get; set; }

    [JsonPropertyName("disableTunMode")]
    public bool DisableTunMode { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
