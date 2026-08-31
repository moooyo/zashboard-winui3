using System.Net.Http.Headers;

namespace Zashboard.Infrastructure.Tests;

internal sealed record CapturedHttpRequest(
    HttpMethod Method,
    Uri Uri,
    AuthenticationHeaderValue? Authorization,
    IReadOnlyList<string> AcceptMediaTypes,
    string? ContentType,
    string? Content);

internal sealed class RecordingHttpMessageHandler(
    Func<CapturedHttpRequest, HttpResponseMessage> responder) : HttpMessageHandler
{
    public List<CapturedHttpRequest> Requests { get; } = [];

    public CapturedHttpRequest LastRequest => Requests.Count == 0
        ? throw new InvalidOperationException("No HTTP request has been captured.")
        : Requests[^1];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Uri uri = request.RequestUri
            ?? throw new InvalidOperationException("The HTTP request does not have a URI.");
        string? content = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        CapturedHttpRequest captured = new(
            request.Method,
            uri,
            request.Headers.Authorization,
            request.Headers.Accept.Select(static value => value.MediaType ?? string.Empty).ToArray(),
            request.Content?.Headers.ContentType?.ToString(),
            content);

        Requests.Add(captured);
        return responder(captured);
    }
}
