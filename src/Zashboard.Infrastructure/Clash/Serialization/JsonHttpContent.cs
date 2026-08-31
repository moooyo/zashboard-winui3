using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Zashboard.Infrastructure.Clash.Serialization;

internal static class JsonHttpContent
{
    private static readonly MediaTypeHeaderValue JsonMediaType = new("application/json")
    {
        CharSet = "utf-8",
    };

    public static HttpContent Create<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        ByteArrayContent content = new(payload);
        content.Headers.ContentType = JsonMediaType;
        return content;
    }
}
