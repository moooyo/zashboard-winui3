using System.Net;

namespace Zashboard.Infrastructure.Clash;

public class ClashApiException : Exception
{
    public ClashApiException(
        string message,
        HttpStatusCode? statusCode = null,
        Uri? requestUri = null,
        string? responseBody = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        RequestUri = requestUri is null ? null : SensitiveDataRedactor.Redact(requestUri);
        ResponseBody = responseBody;
    }

    public HttpStatusCode? StatusCode { get; }

    public Uri? RequestUri { get; }

    public string? ResponseBody { get; }
}

public sealed class ClashAuthenticationException : ClashApiException
{
    public ClashAuthenticationException(Uri? requestUri, string? responseBody = null)
        : this(HttpStatusCode.Unauthorized, requestUri, responseBody)
    {
    }

    public ClashAuthenticationException(
        HttpStatusCode statusCode,
        Uri? requestUri,
        string? responseBody = null)
        : base(
            "The Clash API rejected the backend credential.",
            statusCode,
            requestUri,
            responseBody)
    {
        if (statusCode is not HttpStatusCode.Unauthorized and not HttpStatusCode.Forbidden)
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode));
        }
    }
}

public sealed class ClashStreamUnsupportedException : ClashApiException
{
    public ClashStreamUnsupportedException(HttpStatusCode statusCode, Uri? requestUri)
        : base(
            "The Clash API does not support this WebSocket stream.",
            statusCode,
            requestUri)
    {
        if (statusCode is not
            HttpStatusCode.BadRequest and not
            HttpStatusCode.NotFound and not
            HttpStatusCode.MethodNotAllowed)
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode));
        }
    }
}

public sealed class ClashProtocolException : ClashApiException
{
    public ClashProtocolException(string message, Uri? requestUri, Exception? innerException = null)
        : base(message, requestUri: requestUri, innerException: innerException)
    {
    }
}
