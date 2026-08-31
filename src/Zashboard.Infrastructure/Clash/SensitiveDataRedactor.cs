using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Zashboard.Infrastructure.Clash;

internal static class SensitiveDataRedactor
{
    public static Uri Redact(Uri uri)
    {
        string[] pairs = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        List<string> safePairs = new(pairs.Length);

        foreach (string pair in pairs)
        {
            int separator = pair.IndexOf('=');
            string rawName = separator >= 0 ? pair[..separator] : pair;
            string name = Uri.UnescapeDataString(rawName);

            if (name.Equals("token", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("secret", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("password", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("url", StringComparison.OrdinalIgnoreCase))
            {
                safePairs.Add($"{rawName}=%5Bredacted%5D");
            }
            else
            {
                safePairs.Add(pair);
            }
        }

        UriBuilder builder = new(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Join('&', safePairs),
        };

        return builder.Uri;
    }

    public static string? RedactResponseBody(string? body, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            ArrayBufferWriter<byte> buffer = new();
            using (Utf8JsonWriter writer = new(buffer))
            {
                WriteRedacted(writer, document.RootElement, secret);
            }

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        catch (JsonException)
        {
            // Unstructured error bodies are omitted because their secret-bearing shape is unknown.
            return null;
        }
    }

    private static void WriteRedacted(
        Utf8JsonWriter writer,
        JsonElement element,
        string secret)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (IsSensitiveName(property.Name))
                    {
                        writer.WriteStringValue("[redacted]");
                    }
                    else
                    {
                        WriteRedacted(writer, property.Value, secret);
                    }
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteRedacted(writer, item, secret);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(RedactString(element.GetString() ?? string.Empty, secret));
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static bool IsSensitiveName(string name) =>
        name.Equals("token", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("access_token", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("accessToken", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("secret", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("password", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("credential", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("url", StringComparison.OrdinalIgnoreCase);

    private static string RedactString(string value, string secret)
    {
        string redacted = secret.Length == 0
            ? value
            : value.Replace(secret, "[redacted]", StringComparison.Ordinal);

        if (Uri.TryCreate(redacted, UriKind.Absolute, out Uri? uri))
        {
            return Redact(uri).AbsoluteUri;
        }

        string[] sensitiveMarkers =
        [
            "bearer ",
            "?token=",
            "&token=",
            "?secret=",
            "&secret=",
            "?password=",
            "&password=",
            "token%3d",
            "secret%3d",
            "password%3d",
        ];
        if (sensitiveMarkers.Any(marker =>
            redacted.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return "[redacted]";
        }

        int schemeSeparator = redacted.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator >= 0)
        {
            int authorityStart = schemeSeparator + 3;
            int authorityEnd = redacted.IndexOfAny(['/', '?', '#'], authorityStart);
            int userInfoSeparator = redacted.IndexOf('@', authorityStart);
            if (userInfoSeparator >= 0 &&
                (authorityEnd < 0 || userInfoSeparator < authorityEnd))
            {
                return "[redacted]";
            }
        }

        return redacted;
    }
}
