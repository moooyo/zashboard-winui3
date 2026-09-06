using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zashboard.Infrastructure.Clash.Serialization;

internal sealed class StringOrStringArrayJsonConverter : JsonConverter<List<string>>
{
    public override List<string> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return [reader.GetString()!];
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("The Clash GeoIP field must be a string, string array, or null.");
        }

        List<string> values = [];
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return values;
            }

            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("The Clash GeoIP array must contain only strings.");
            }

            values.Add(reader.GetString()!);
        }

        throw new JsonException("The Clash GeoIP array is incomplete.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        List<string> value,
        JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (string item in value)
        {
            writer.WriteStringValue(item);
        }

        writer.WriteEndArray();
    }
}
