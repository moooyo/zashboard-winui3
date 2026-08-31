using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Zashboard.Core.Backends;
using Zashboard.Core.Capabilities;

namespace Zashboard.Infrastructure.Clash.Transport;

internal sealed class ClashHttpTransport
{
    private const int MaximumErrorBodyCharacters = 4096;

    private readonly HttpClient _httpClient;
    private readonly BackendEndpoint _endpoint;
    private readonly string _secret;
    private readonly ICapabilityRegistry _capabilities;
    private readonly ClashHttpOptions _options;
    private readonly TimeProvider _timeProvider;

    public ClashHttpTransport(
        HttpClient httpClient,
        BackendEndpoint endpoint,
        BackendCredential credential,
        ICapabilityRegistry capabilities,
        ClashHttpOptions options,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        ArgumentNullException.ThrowIfNull(credential);
        _secret = credential.Secret;
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Uri BuildUri(
        ReadOnlySpan<string> pathSegments,
        IReadOnlyList<KeyValuePair<string, string?>>? query = null) =>
        ClashUriFactory.Http(_endpoint, pathSegments, query);

    public async Task<TResponse> SendAsync<TResponse>(
        HttpMethod method,
        Uri requestUri,
        JsonTypeInfo<TResponse> responseTypeInfo,
        HttpContent? content = null,
        ClashCapability? capability = null,
        CapabilityNotFoundBehavior notFoundBehavior = CapabilityNotFoundBehavior.Unsupported,
        TimeSpan? minimumTimeout = null,
        CancellationToken cancellationToken = default)
    {
        TimeSpan operationTimeout = ResolveOperationTimeout(minimumTimeout);
        using CancellationTokenSource timeoutSource = new(operationTimeout, _timeProvider);
        using CancellationTokenSource operationSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        try
        {
            using HttpResponseMessage response = await SendCoreAsync(
                method,
                requestUri,
                content,
                capability,
                notFoundBehavior,
                operationSource.Token).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                throw new ClashProtocolException(
                    "The Clash API returned no content for an endpoint that requires JSON.",
                    requestUri);
            }

            try
            {
                Stream stream = await response.Content.ReadAsStreamAsync(operationSource.Token)
                    .ConfigureAwait(false);
                TResponse? value = await JsonSerializer.DeserializeAsync(
                    stream,
                    responseTypeInfo,
                    operationSource.Token).ConfigureAwait(false);

                if (value is null)
                {
                    throw new ClashProtocolException(
                        "The Clash API returned an empty JSON value.",
                        requestUri);
                }

                ObserveSupported(capability);
                return value;
            }
            catch (JsonException exception)
            {
                throw new ClashProtocolException(
                    "The Clash API returned malformed or incompatible JSON.",
                    requestUri,
                    exception);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ClashApiException(
                "The Clash API request timed out.",
                requestUri: requestUri);
        }
    }

    public async Task SendAsync(
        HttpMethod method,
        Uri requestUri,
        HttpContent? content = null,
        ClashCapability? capability = null,
        CapabilityNotFoundBehavior notFoundBehavior = CapabilityNotFoundBehavior.Unsupported,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeoutSource = new(_options.OperationTimeout, _timeProvider);
        using CancellationTokenSource operationSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        try
        {
            using HttpResponseMessage response = await SendCoreAsync(
                method,
                requestUri,
                content,
                capability,
                notFoundBehavior,
                operationSource.Token).ConfigureAwait(false);
            ObserveSupported(capability);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ClashApiException(
                "The Clash API request timed out.",
                requestUri: requestUri);
        }
    }

    private async Task<HttpResponseMessage> SendCoreAsync(
        HttpMethod method,
        Uri requestUri,
        HttpContent? content,
        ClashCapability? capability,
        CapabilityNotFoundBehavior notFoundBehavior,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(method, requestUri)
        {
            Content = content,
        };

        ClashAuthentication.ApplyBearer(request.Headers, _secret);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new ClashApiException(
                "The Clash API request failed before a response was received.",
                exception.StatusCode,
                requestUri,
                innerException: new HttpRequestException(
                    "The HTTP transport failed before a response was received.",
                    inner: null,
                    statusCode: exception.StatusCode));
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        HttpStatusCode statusCode = response.StatusCode;
        string? responseBody;
        try
        {
            ObserveUnsupported(capability, statusCode, notFoundBehavior);
            responseBody = await ReadErrorBodyAsync(response, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            response.Dispose();
        }

        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new ClashAuthenticationException(statusCode, requestUri, responseBody);
        }

        throw new ClashApiException(
            $"The Clash API returned HTTP {(int)statusCode} ({statusCode}).",
            statusCode,
            requestUri,
            responseBody);
    }

    private async Task<string?> ReadErrorBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength == 0)
        {
            return null;
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using StreamReader reader = new(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: false);
        char[] buffer = new char[MaximumErrorBodyCharacters];
        int charactersRead = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        string body = new(buffer, 0, charactersRead);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        return SensitiveDataRedactor.RedactResponseBody(body, _secret);
    }

    private TimeSpan ResolveOperationTimeout(TimeSpan? minimumTimeout)
    {
        if (!minimumTimeout.HasValue)
        {
            return _options.OperationTimeout;
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(minimumTimeout.Value, TimeSpan.Zero);
        return minimumTimeout.Value > _options.OperationTimeout
            ? minimumTimeout.Value
            : _options.OperationTimeout;
    }

    private void ObserveSupported(ClashCapability? capability)
    {
        if (capability is not ClashCapability value)
        {
            return;
        }

        _capabilities.Observe(
            value,
            CapabilitySupport.Supported,
            CapabilityEvidenceKind.SuccessfulCall,
            _timeProvider.GetUtcNow());
    }

    private void ObserveUnsupported(
        ClashCapability? capability,
        HttpStatusCode statusCode,
        CapabilityNotFoundBehavior notFoundBehavior)
    {
        if (capability is not ClashCapability value)
        {
            return;
        }

        CapabilityEvidenceKind? evidence = statusCode switch
        {
            HttpStatusCode.NotFound
                when notFoundBehavior == CapabilityNotFoundBehavior.Unsupported =>
                CapabilityEvidenceKind.EndpointNotFound,
            HttpStatusCode.MethodNotAllowed => CapabilityEvidenceKind.MethodNotAllowed,
            _ => null,
        };

        if (evidence.HasValue)
        {
            _capabilities.Observe(
                value,
                CapabilitySupport.Unsupported,
                evidence.Value,
                _timeProvider.GetUtcNow(),
                $"HTTP {(int)statusCode}");
        }
    }
}

internal enum CapabilityNotFoundBehavior
{
    Unsupported,
    Inconclusive,
}
