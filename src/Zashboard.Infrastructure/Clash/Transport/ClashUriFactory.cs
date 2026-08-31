using System.Globalization;
using System.Text;
using Zashboard.Core.Backends;

namespace Zashboard.Infrastructure.Clash.Transport;

internal static class ClashUriFactory
{
    public static Uri Http(
        BackendEndpoint endpoint,
        ReadOnlySpan<string> pathSegments,
        IReadOnlyList<KeyValuePair<string, string?>>? query = null) =>
        Build(endpoint.BaseUri, false, pathSegments, query);

    public static Uri WebSocket(
        BackendEndpoint endpoint,
        ReadOnlySpan<string> pathSegments,
        IReadOnlyList<KeyValuePair<string, string?>>? query = null) =>
        Build(endpoint.BaseUri, true, pathSegments, query);

    public static KeyValuePair<string, string?> Query(string name, string? value) => new(name, value);

    public static KeyValuePair<string, string?> Query(string name, int value) =>
        new(name, value.ToString(CultureInfo.InvariantCulture));

    public static KeyValuePair<string, string?> Query(string name, bool value) =>
        new(name, value ? "true" : "false");

    private static Uri Build(
        Uri baseUri,
        bool webSocket,
        ReadOnlySpan<string> pathSegments,
        IReadOnlyList<KeyValuePair<string, string?>>? query)
    {
        UriBuilder builder = new(baseUri);
        if (webSocket)
        {
            builder.Scheme = baseUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? "wss"
                : "ws";
        }

        StringBuilder path = new(baseUri.AbsolutePath.TrimEnd('/'));
        foreach (string segment in pathSegments)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(segment);
            path.Append('/').Append(Uri.EscapeDataString(segment));
        }

        builder.Path = path.Length == 0 ? "/" : path.ToString();
        builder.Query = BuildQuery(query);
        builder.Fragment = string.Empty;
        return builder.Uri;
    }

    private static string BuildQuery(IReadOnlyList<KeyValuePair<string, string?>>? query)
    {
        if (query is null || query.Count == 0)
        {
            return string.Empty;
        }

        StringBuilder value = new();
        foreach (KeyValuePair<string, string?> pair in query)
        {
            if (pair.Value is null)
            {
                continue;
            }

            if (value.Length > 0)
            {
                value.Append('&');
            }

            value.Append(Uri.EscapeDataString(pair.Key));
            value.Append('=');
            value.Append(Uri.EscapeDataString(pair.Value));
        }

        return value.ToString();
    }
}
