using System.Text.Json.Serialization;
using Zashboard.Infrastructure.Persistence.Wire;

namespace Zashboard.Infrastructure.Persistence.Serialization;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true)]
[JsonSerializable(typeof(BackendProfilesDocumentDto))]
internal sealed partial class PersistenceJsonContext : JsonSerializerContext;
