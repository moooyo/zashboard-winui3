namespace Zashboard.Core.Backends;

public sealed class BackendEndpoint : IEquatable<BackendEndpoint>
{
    private BackendEndpoint(Uri baseUri)
    {
        BaseUri = baseUri;
    }

    public Uri BaseUri { get; }

    public static BackendEndpoint Create(string address)
    {
        if (!TryCreate(address, out BackendEndpoint? endpoint, out string? error))
        {
            throw new ArgumentException(error, nameof(address));
        }

        return endpoint!;
    }

    public static BackendEndpoint FromParts(
        string scheme,
        string host,
        int? port = null,
        string? basePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        string normalizedScheme = scheme.Trim().ToLowerInvariant();
        if (!IsSupportedScheme(normalizedScheme))
        {
            throw new ArgumentException("Only HTTP and HTTPS backend endpoints are supported.", nameof(scheme));
        }

        if (port is <= 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "The port must be between 1 and 65535.");
        }

        string normalizedHost = host.Trim().TrimStart('[').TrimEnd(']');
        UriBuilder builder = new(normalizedScheme, normalizedHost)
        {
            Path = NormalizeBasePath(basePath),
        };

        if (port.HasValue)
        {
            builder.Port = port.Value;
        }

        return new BackendEndpoint(Normalize(builder.Uri));
    }

    public static bool TryCreate(
        string? address,
        out BackendEndpoint? endpoint,
        out string? error)
    {
        endpoint = null;
        error = null;

        if (string.IsNullOrWhiteSpace(address))
        {
            error = "The backend address is required.";
            return false;
        }

        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out Uri? candidate))
        {
            error = "The backend address must be an absolute HTTP or HTTPS URI.";
            return false;
        }

        if (!IsSupportedScheme(candidate.Scheme))
        {
            error = "Only HTTP and HTTPS backend endpoints are supported.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(candidate.Host))
        {
            error = "The backend address must contain a host.";
            return false;
        }

        if (!string.IsNullOrEmpty(candidate.UserInfo))
        {
            error = "Credentials must not be embedded in the backend address.";
            return false;
        }

        if (!string.IsNullOrEmpty(candidate.Query) || !string.IsNullOrEmpty(candidate.Fragment))
        {
            error = "The backend address must not contain a query string or fragment.";
            return false;
        }

        endpoint = new BackendEndpoint(Normalize(candidate));
        return true;
    }

    public Uri Resolve(params ReadOnlySpan<string> pathSegments)
    {
        UriBuilder builder = new(BaseUri);
        string path = BaseUri.AbsolutePath.TrimEnd('/');

        foreach (string segment in pathSegments)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(segment);
            path += "/" + Uri.EscapeDataString(segment.Trim('/'));
        }

        builder.Path = path.Length == 0 ? "/" : path;
        return builder.Uri;
    }

    public Uri GetWebSocketBaseUri()
    {
        UriBuilder builder = new(BaseUri)
        {
            Scheme = BaseUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? "wss"
                : "ws",
        };

        return builder.Uri;
    }

    public bool Equals(BackendEndpoint? other) =>
        other is not null &&
        BaseUri.Port == other.BaseUri.Port &&
        string.Equals(BaseUri.Scheme, other.BaseUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(BaseUri.IdnHost, other.BaseUri.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(BaseUri.AbsolutePath, other.BaseUri.AbsolutePath, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as BackendEndpoint);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(BaseUri.Scheme, StringComparer.OrdinalIgnoreCase);
        hash.Add(BaseUri.IdnHost, StringComparer.OrdinalIgnoreCase);
        hash.Add(BaseUri.Port);
        hash.Add(BaseUri.AbsolutePath, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    public override string ToString() => BaseUri.AbsoluteUri;

    private static bool IsSupportedScheme(string scheme) =>
        scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static Uri Normalize(Uri uri)
    {
        UriBuilder builder = new(uri)
        {
            Scheme = uri.Scheme.ToLowerInvariant(),
            Host = uri.IdnHost.ToLowerInvariant(),
            Path = NormalizeBasePath(uri.AbsolutePath),
            Query = string.Empty,
            Fragment = string.Empty,
        };

        if (uri.IsDefaultPort)
        {
            builder.Port = -1;
        }

        return builder.Uri;
    }

    private static string NormalizeBasePath(string? basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath) || basePath == "/")
        {
            return "/";
        }

        string[] segments = basePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return $"/{string.Join('/', segments)}/";
    }
}
