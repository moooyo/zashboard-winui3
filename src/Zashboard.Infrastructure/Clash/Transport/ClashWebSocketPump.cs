using System.Buffers;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Zashboard.Core.Backends;
using Zashboard.Core.Capabilities;
using Zashboard.Core.Clash;

namespace Zashboard.Infrastructure.Clash.Transport;

internal sealed class ClashWebSocketPump
{
    private readonly BackendEndpoint _endpoint;
    private readonly string _secret;
    private readonly ICapabilityRegistry _capabilities;
    private readonly ClashWebSocketOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationToken _sessionLifetime;
    private readonly Action<ClashStreamStatus> _publishStatus;
    private readonly Action<ClashStreamItemsDroppedEventArgs> _publishItemsDropped;

    public ClashWebSocketPump(
        BackendEndpoint endpoint,
        BackendCredential credential,
        ICapabilityRegistry capabilities,
        ClashWebSocketOptions options,
        Action<ClashStreamStatus> publishStatus,
        Action<ClashStreamItemsDroppedEventArgs> publishItemsDropped,
        TimeProvider? timeProvider,
        CancellationToken sessionLifetime)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        ArgumentNullException.ThrowIfNull(credential);
        _secret = credential.Secret;
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _sessionLifetime = sessionLifetime;
        _publishStatus = publishStatus ?? throw new ArgumentNullException(nameof(publishStatus));
        _publishItemsDropped = publishItemsDropped ??
            throw new ArgumentNullException(nameof(publishItemsDropped));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public IAsyncEnumerable<TDomain> StreamAsync<TWire, TDomain>(
        ClashStreamKind streamKind,
        string[] pathSegments,
        IReadOnlyList<KeyValuePair<string, string?>>? query,
        JsonTypeInfo<TWire> typeInfo,
        Func<TWire, TDomain> map,
        int capacity,
        ClashCapability? capability,
        CancellationToken cancellationToken) =>
        StreamCoreAsync(
            streamKind,
            pathSegments,
            query,
            typeInfo,
            map,
            capacity,
            capability,
            cancellationToken);

    private async IAsyncEnumerable<TDomain> StreamCoreAsync<TWire, TDomain>(
        ClashStreamKind streamKind,
        string[] pathSegments,
        IReadOnlyList<KeyValuePair<string, string?>>? query,
        JsonTypeInfo<TWire> typeInfo,
        Func<TWire, TDomain> map,
        int capacity,
        ClashCapability? capability,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            _sessionLifetime,
            cancellationToken);
        long droppedCount = 0;

        Channel<TDomain> channel = Channel.CreateBounded<TDomain>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        }, _ => IncrementSaturating(ref droppedCount));

        Task producer = ProduceAsync(
            channel.Writer,
            streamKind,
            pathSegments,
            query,
            typeInfo,
            map,
            capability,
            lifetime.Token);

        try
        {
            await foreach (TDomain item in channel.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                PublishDroppedItems(streamKind, Interlocked.Exchange(ref droppedCount, 0));
                yield return item;
            }
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            try
            {
                await producer.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            finally
            {
                PublishDroppedItems(streamKind, Interlocked.Exchange(ref droppedCount, 0));
            }
        }
    }

    private async Task ProduceAsync<TWire, TDomain>(
        ChannelWriter<TDomain> writer,
        ClashStreamKind streamKind,
        string[] pathSegments,
        IReadOnlyList<KeyValuePair<string, string?>>? query,
        JsonTypeInfo<TWire> typeInfo,
        Func<TWire, TDomain> map,
        ClashCapability? capability,
        CancellationToken cancellationToken)
    {
        Exception? terminalError = null;
        int attempt = 0;
        int consecutiveProtocolFailures = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                PublishStatus(
                    streamKind,
                    ClashStreamState.Connecting,
                    attempt,
                    detail: attempt == 0 ? null : "Reconnecting to the stream.");

                try
                {
                    await ReceiveConnectionAsync(
                        writer,
                        streamKind,
                        pathSegments,
                        query,
                        typeInfo,
                        map,
                        capability,
                        () =>
                        {
                            attempt = 0;
                            consecutiveProtocolFailures = 0;
                        },
                        cancellationToken).ConfigureAwait(false);

                    if (!cancellationToken.IsCancellationRequested)
                    {
                        attempt++;
                        PublishStatus(
                            streamKind,
                            ClashStreamState.Retrying,
                            attempt,
                            detail: "The remote endpoint closed the stream.");
                    }
                }
                catch (Exception exception) when (
                    (exception is OperationCanceledException or WebSocketException) &&
                    cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (TimeoutException exception)
                {
                    attempt++;
                    PublishStatus(
                        streamKind,
                        ClashStreamState.Retrying,
                        attempt,
                        detail: exception.Message);
                }
                catch (ClashAuthenticationException exception)
                {
                    PublishStatus(
                        streamKind,
                        ClashStreamState.Unauthorized,
                        attempt,
                        (int?)exception.StatusCode,
                        exception.Message);
                    terminalError = exception;
                    break;
                }
                catch (ClashStreamUnsupportedException exception)
                {
                    ObserveUnsupported(capability, exception.StatusCode!.Value);
                    PublishStatus(
                        streamKind,
                        ClashStreamState.Unsupported,
                        attempt,
                        (int)exception.StatusCode.Value,
                        exception.Message);
                    terminalError = exception;
                    break;
                }
                catch (ClashProtocolException exception)
                {
                    attempt++;
                    consecutiveProtocolFailures++;
                    if (consecutiveProtocolFailures >= _options.MaximumConsecutiveProtocolFailures)
                    {
                        PublishStatus(
                            streamKind,
                            ClashStreamState.Faulted,
                            attempt,
                            detail: "The stream repeatedly returned invalid protocol data.");
                        terminalError = exception;
                        break;
                    }

                    PublishStatus(
                        streamKind,
                        ClashStreamState.Retrying,
                        attempt,
                        detail: "The stream returned invalid protocol data; retrying.");
                }
                catch (WebSocketException)
                {
                    attempt++;
                    PublishStatus(
                        streamKind,
                        ClashStreamState.Retrying,
                        attempt,
                        detail: "The WebSocket connection failed; retrying.");
                }
                catch (HttpRequestException)
                {
                    attempt++;
                    PublishStatus(
                        streamKind,
                        ClashStreamState.Retrying,
                        attempt,
                        detail: "The WebSocket handshake failed; retrying.");
                }
                catch (Exception exception)
                {
                    PublishStatus(
                        streamKind,
                        ClashStreamState.Faulted,
                        attempt,
                        detail: "The stream stopped because of an unexpected error.");
                    terminalError = exception;
                    break;
                }

                TimeSpan delay = GetReconnectDelay(attempt);
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            terminalError = exception;
        }
        finally
        {
            writer.TryComplete(terminalError);
        }
    }

    private async Task ReceiveConnectionAsync<TWire, TDomain>(
        ChannelWriter<TDomain> writer,
        ClashStreamKind streamKind,
        string[] pathSegments,
        IReadOnlyList<KeyValuePair<string, string?>>? query,
        JsonTypeInfo<TWire> typeInfo,
        Func<TWire, TDomain> map,
        ClashCapability? capability,
        Action markHealthy,
        CancellationToken cancellationToken)
    {
        List<KeyValuePair<string, string?>> effectiveQuery = query is null
            ? []
            : new List<KeyValuePair<string, string?>>(query);
        effectiveQuery.Add(ClashUriFactory.Query("token", _secret));

        Uri uri = ClashUriFactory.WebSocket(_endpoint, pathSegments, effectiveQuery);
        using ClientWebSocket socket = new();
        // Some Clash-compatible endpoints only write frames and never process ping requests.
        // Detect stalled periodic streams with message deadlines instead of requiring pong replies.
        socket.Options.KeepAliveInterval = _options.KeepAliveInterval;
        socket.Options.CollectHttpResponseDetails = true;
        using CancellationTokenSource handshakeTimeout = new(_options.HandshakeTimeout, _timeProvider);
        using CancellationTokenSource handshakeLifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            handshakeTimeout.Token);
        try
        {
            await socket.ConnectAsync(uri, handshakeLifetime.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            (exception is OperationCanceledException or WebSocketException) &&
            !cancellationToken.IsCancellationRequested && handshakeTimeout.IsCancellationRequested)
        {
            throw new TimeoutException("The WebSocket handshake timed out; retrying.");
        }
        catch (WebSocketException) when (socket.HttpStatusCode is
            HttpStatusCode.Unauthorized or
            HttpStatusCode.Forbidden)
        {
            throw new ClashAuthenticationException(socket.HttpStatusCode, uri);
        }
        catch (WebSocketException) when (socket.HttpStatusCode is
            HttpStatusCode.BadRequest or
            HttpStatusCode.NotFound or
            HttpStatusCode.MethodNotAllowed)
        {
            throw new ClashStreamUnsupportedException(socket.HttpStatusCode, uri);
        }

        handshakeTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
        ObserveSupported(capability);
        PublishStatus(streamKind, ClashStreamState.Connected, retryAttempt: 0);

        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            TWire? wire = await ReceiveMessageAsync(socket, streamKind, typeInfo, uri, cancellationToken)
                .ConfigureAwait(false);
            if (wire is null)
            {
                return;
            }

            await writer.WriteAsync(map(wire), cancellationToken).ConfigureAwait(false);
            markHealthy();
        }
    }

    private async Task<T?> ReceiveMessageAsync<T>(
        ClientWebSocket socket,
        ClashStreamKind streamKind,
        JsonTypeInfo<T> typeInfo,
        Uri requestUri,
        CancellationToken cancellationToken)
    {
        bool awaitingFirstLogFragment = streamKind == ClashStreamKind.Logs;
        using CancellationTokenSource messageTimeout = new(
            awaitingFirstLogFragment ? Timeout.InfiniteTimeSpan : _options.MessageTimeout,
            _timeProvider);
        using CancellationTokenSource messageLifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            messageTimeout.Token);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(_options.ReceiveBufferSize);
        try
        {
            using MemoryStream message = new();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    messageLifetime.Token).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return default;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    throw new ClashProtocolException(
                        "The Clash WebSocket returned a non-text message.",
                        requestUri);
                }

                if (message.Length + result.Count > _options.MaximumMessageSize)
                {
                    throw new ClashProtocolException(
                        "The Clash WebSocket message exceeded the configured size limit.",
                        requestUri);
                }

                message.Write(buffer, 0, result.Count);
                if (awaitingFirstLogFragment && !result.EndOfMessage)
                {
                    // Logs may be silent indefinitely, but an in-progress message must complete.
                    messageTimeout.CancelAfter(_options.MessageTimeout);
                    awaitingFirstLogFragment = false;
                }
            }
            while (!result.EndOfMessage);

            if (message.Length == 0)
            {
                throw new ClashProtocolException(
                    "The Clash WebSocket returned an empty message.",
                    requestUri);
            }

            ReadOnlySpan<byte> payload = message.GetBuffer().AsSpan(0, checked((int)message.Length));
            try
            {
                T? value = JsonSerializer.Deserialize(payload, typeInfo);
                return value ?? throw new ClashProtocolException(
                    "The Clash WebSocket returned an empty JSON value.",
                    requestUri);
            }
            catch (JsonException exception)
            {
                throw new ClashProtocolException(
                    "The Clash WebSocket returned malformed or incompatible JSON.",
                    requestUri,
                    exception);
            }
        }
        catch (Exception exception) when (
            (exception is OperationCanceledException or WebSocketException) &&
            !cancellationToken.IsCancellationRequested && messageTimeout.IsCancellationRequested)
        {
            throw new TimeoutException("The WebSocket message timed out; retrying.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private TimeSpan GetReconnectDelay(int attempt)
    {
        if (attempt <= 0 || _options.MinimumReconnectDelay == TimeSpan.Zero)
        {
            return _options.MinimumReconnectDelay;
        }

        int exponent = Math.Min(attempt - 1, 10);
        double milliseconds = Math.Min(
            _options.MaximumReconnectDelay.TotalMilliseconds,
            _options.MinimumReconnectDelay.TotalMilliseconds * Math.Pow(2, exponent));
        double jitter = 0.8d + (Random.Shared.NextDouble() * 0.4d);
        return TimeSpan.FromMilliseconds(Math.Min(
            _options.MaximumReconnectDelay.TotalMilliseconds,
            milliseconds * jitter));
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

    private void ObserveUnsupported(ClashCapability? capability, HttpStatusCode statusCode)
    {
        if (capability is not ClashCapability value)
        {
            return;
        }

        CapabilityEvidenceKind evidence = statusCode switch
        {
            HttpStatusCode.BadRequest => CapabilityEvidenceKind.BadRequest,
            HttpStatusCode.MethodNotAllowed => CapabilityEvidenceKind.MethodNotAllowed,
            _ => CapabilityEvidenceKind.EndpointNotFound,
        };
        _capabilities.Observe(
            value,
            CapabilitySupport.Unsupported,
            evidence,
            _timeProvider.GetUtcNow(),
            $"HTTP {(int)statusCode}");
    }

    private void PublishStatus(
        ClashStreamKind kind,
        ClashStreamState state,
        int retryAttempt,
        int? httpStatusCode = null,
        string? detail = null) =>
        _publishStatus(new ClashStreamStatus(
            kind,
            state,
            retryAttempt,
            _timeProvider.GetUtcNow(),
            httpStatusCode,
            detail));

    private void PublishDroppedItems(ClashStreamKind kind, long count)
    {
        if (count > 0)
        {
            _publishItemsDropped(new ClashStreamItemsDroppedEventArgs(kind, count));
        }
    }

    private static void IncrementSaturating(ref long value)
    {
        long current = Volatile.Read(ref value);
        while (current < long.MaxValue)
        {
            long observed = Interlocked.CompareExchange(ref value, current + 1, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }
}
