using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Zashboard.Core.Backends;
using Zashboard.Core.Capabilities;
using Zashboard.Core.Clash;
using Zashboard.Infrastructure.Clash;

namespace Zashboard.Infrastructure.Tests;

[TestClass]
public sealed class ClashStreamClientTests
{
    [TestMethod]
    public async Task ConnectionsStreamMapsSuccessfulFrameAndUsesAuthenticatedBasePath()
    {
        await using RawWebSocketServer server = RawWebSocketServer.SendText(
            """
            {
              "connections": [
                {
                  "id": "connection-1",
                  "download": "10",
                  "upload": 20,
                  "chains": ["Proxy A"],
                  "rule": "MATCH",
                  "rulePayload": "",
                  "start": "2026-08-30T04:05:06Z",
                  "metadata": {
                    "host": "example.com",
                    "destinationPort": "443",
                    "network": "udp",
                    "smartBlock": "normal"
                  },
                  "futureField": true
                }
              ],
              "downloadTotal": "100",
              "uploadTotal": 200,
              "memory": 300
            }
            """,
            connectionCount: 1);
        ClashStreamClient client = CreateClient(server.Endpoint, maximumProtocolFailures: 2);
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(10));

        ConnectionStreamSnapshot snapshot = await ReadFirstAsync(
            client.StreamConnectionsAsync(guard.Token),
            guard.Token);

        ClashConnection connection = snapshot.Connections.Single();
        Assert.AreEqual("connection-1", connection.Id);
        Assert.AreEqual(10L, connection.Download);
        Assert.AreEqual(20L, connection.Upload);
        Assert.AreEqual("Proxy A", connection.Chains.Single());
        Assert.AreEqual("example.com", connection.Metadata.Host);
        Assert.AreEqual("normal", connection.Metadata.SmartBlock);
        Assert.AreEqual(
            new DateTimeOffset(2026, 8, 30, 4, 5, 6, TimeSpan.Zero),
            connection.StartedAt);
        Assert.AreEqual(100L, snapshot.DownloadTotal);
        Assert.AreEqual(200L, snapshot.UploadTotal);
        Assert.AreEqual(300L, snapshot.Memory);
        await AssertRequestAsync(server, "/api/connections", "token=stream-secret");
    }

    [TestMethod]
    public async Task LogStreamMapsSuccessfulFrameAndObservesTraceCapability()
    {
        await using RawWebSocketServer server = RawWebSocketServer.SendText(
            "{\"type\":\"warning\",\"payload\":\"A log message\"}",
            connectionCount: 1);
        CapabilityRegistry capabilities = new();
        ClashStreamClient client = CreateClient(
            server.Endpoint,
            maximumProtocolFailures: 2,
            capabilities);
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(10));

        ClashLogMessage message = await ReadFirstAsync(
            client.StreamLogsAsync(ClashLogLevel.Trace, guard.Token),
            guard.Token);

        Assert.AreEqual(ClashLogLevel.Warning, message.Level);
        Assert.AreEqual("warning", message.RawLevel);
        Assert.AreEqual("A log message", message.Payload);
        Assert.AreEqual(
            CapabilitySupport.Supported,
            capabilities.GetObservation(ClashCapability.TraceLogLevel).Support);
        await AssertRequestAsync(
            server,
            "/api/logs",
            "level=trace",
            "token=stream-secret");
    }

    [TestMethod]
    public async Task LogStreamPublishesAggregatedDroppedItemsBeforeNextItem()
    {
        await using RawWebSocketServer server = CreateGatedLogOverflowServer();
        ConcurrentQueue<ClashStreamItemsDroppedEventArgs> droppedEvents = new();
        TaskCompletionSource faulted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ClashStreamClient client = CreateClient(
            server.Endpoint,
            maximumProtocolFailures: 1,
            logCapacity: 1);
        client.StreamItemsDropped += (_, args) => droppedEvents.Enqueue(args);
        client.StreamStatusChanged += (_, args) =>
        {
            if (args.Status is { Kind: ClashStreamKind.Logs, State: ClashStreamState.Faulted })
            {
                faulted.TrySetResult();
            }
        };
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(10));
        await using IAsyncEnumerator<ClashLogMessage> enumerator = client
            .StreamLogsAsync(ClashLogLevel.Info, guard.Token)
            .GetAsyncEnumerator(guard.Token);

        Assert.IsTrue(await enumerator.MoveNextAsync());
        Assert.AreEqual("one", enumerator.Current.Payload);
        server.ReleaseRemainingConnections();
        await faulted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsEmpty(droppedEvents);

        Assert.IsTrue(await enumerator.MoveNextAsync());
        Assert.AreEqual("four", enumerator.Current.Payload);
        ClashStreamItemsDroppedEventArgs dropped = droppedEvents.Single();
        Assert.AreEqual(ClashStreamKind.Logs, dropped.Kind);
        Assert.AreEqual(2L, dropped.Count);

        Exception exception = await CaptureExceptionAsync(enumerator.MoveNextAsync().AsTask());
        Assert.IsInstanceOfType<ClashProtocolException>(exception);
        Assert.HasCount(1, droppedEvents);
    }

    [TestMethod]
    public async Task DisposingLogStreamPublishesPendingDroppedItems()
    {
        await using RawWebSocketServer server = CreateGatedLogOverflowServer();
        ConcurrentQueue<ClashStreamItemsDroppedEventArgs> droppedEvents = new();
        TaskCompletionSource faulted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ClashStreamClient client = CreateClient(
            server.Endpoint,
            maximumProtocolFailures: 1,
            logCapacity: 1);
        client.StreamItemsDropped += (_, args) => droppedEvents.Enqueue(args);
        client.StreamStatusChanged += (_, args) =>
        {
            if (args.Status is { Kind: ClashStreamKind.Logs, State: ClashStreamState.Faulted })
            {
                faulted.TrySetResult();
            }
        };
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(10));

        await using (IAsyncEnumerator<ClashLogMessage> enumerator = client
            .StreamLogsAsync(ClashLogLevel.Info, guard.Token)
            .GetAsyncEnumerator(guard.Token))
        {
            Assert.IsTrue(await enumerator.MoveNextAsync());
            Assert.AreEqual("one", enumerator.Current.Payload);
            server.ReleaseRemainingConnections();
            await faulted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsEmpty(droppedEvents);
        }

        ClashStreamItemsDroppedEventArgs dropped = droppedEvents.Single();
        Assert.AreEqual(ClashStreamKind.Logs, dropped.Kind);
        Assert.AreEqual(2L, dropped.Count);
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(-1L)]
    public void StreamItemsDroppedEventArgsRejectsNonPositiveCount(long count)
    {
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new ClashStreamItemsDroppedEventArgs(ClashStreamKind.Logs, count));
    }

    [TestMethod]
    public async Task TrafficStreamMapsSuccessfulFrame()
    {
        await using RawWebSocketServer server = RawWebSocketServer.SendText(
            "{\"down\":\"34\",\"up\":12,\"downTotal\":\"78\",\"upTotal\":56}",
            connectionCount: 1);
        ClashStreamClient client = CreateClient(server.Endpoint, maximumProtocolFailures: 2);
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(10));

        ClashTrafficSample sample = await ReadFirstAsync(
            client.StreamTrafficAsync(guard.Token),
            guard.Token);

        Assert.AreEqual(34L, sample.Down);
        Assert.AreEqual(12L, sample.Up);
        Assert.AreEqual(78L, sample.DownTotal);
        Assert.AreEqual(56L, sample.UpTotal);
        await AssertRequestAsync(server, "/api/traffic", "token=stream-secret");
    }

    [TestMethod]
    public async Task MemoryStreamMapsSuccessfulFrame()
    {
        await using RawWebSocketServer server = RawWebSocketServer.SendText(
            "{\"inuse\":\"1024\"}",
            connectionCount: 1);
        ClashStreamClient client = CreateClient(server.Endpoint, maximumProtocolFailures: 2);
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(10));

        ClashMemorySample sample = await ReadFirstAsync(
            client.StreamMemoryAsync(guard.Token),
            guard.Token);

        Assert.AreEqual(1024L, sample.InUse);
        await AssertRequestAsync(server, "/api/memory", "token=stream-secret");
    }

    [TestMethod]
    [DataRow(401, ClashStreamState.Unauthorized)]
    [DataRow(403, ClashStreamState.Unauthorized)]
    [DataRow(400, ClashStreamState.Unsupported)]
    [DataRow(404, ClashStreamState.Unsupported)]
    [DataRow(405, ClashStreamState.Unsupported)]
    public async Task HandshakeHttpStatusPublishesTerminalStreamState(
        int statusCode,
        ClashStreamState expectedState)
    {
        await using RawWebSocketServer server = RawWebSocketServer.Reject((HttpStatusCode)statusCode);
        ConcurrentQueue<ClashStreamStatus> statuses = new();
        ClashStreamClient client = CreateClient(server.Endpoint, maximumProtocolFailures: 2);
        client.StreamStatusChanged += (_, args) => statuses.Enqueue(args.Status);
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(10));

        Exception exception = await CaptureExceptionAsync(
            client.StreamTrafficAsync(guard.Token),
            guard.Token);

        if (expectedState == ClashStreamState.Unauthorized)
        {
            Assert.IsInstanceOfType<ClashAuthenticationException>(exception);
        }
        else
        {
            Assert.IsInstanceOfType<ClashStreamUnsupportedException>(exception);
        }

        ClashStreamStatus terminal = statuses.Last();
        Assert.AreEqual(ClashStreamKind.Traffic, terminal.Kind);
        Assert.AreEqual(expectedState, terminal.State);
        Assert.AreEqual(statusCode, terminal.HttpStatusCode);
        CollectionAssert.Contains(
            statuses.Select(static status => status.State).ToArray(),
            ClashStreamState.Connecting);
        Assert.IsFalse(exception.ToString().Contains("stream-secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task TraceLogBadRequestRecordsDistinctCapabilityEvidence()
    {
        await using RawWebSocketServer server = RawWebSocketServer.Reject(HttpStatusCode.BadRequest);
        CapabilityRegistry capabilities = new();
        ClashStreamClient client = CreateClient(
            server.Endpoint,
            maximumProtocolFailures: 1,
            capabilities);
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(10));

        Exception exception = await CaptureExceptionAsync(
            client.StreamLogsAsync(ClashLogLevel.Trace, guard.Token),
            guard.Token);

        Assert.IsInstanceOfType<ClashStreamUnsupportedException>(exception);
        CapabilityObservation observation = capabilities.GetObservation(
            ClashCapability.TraceLogLevel);
        Assert.AreEqual(CapabilitySupport.Unsupported, observation.Support);
        Assert.AreEqual(CapabilityEvidenceKind.BadRequest, observation.Evidence);
    }

    [TestMethod]
    [DataRow("connections")]
    [DataRow("logs")]
    [DataRow("traffic")]
    [DataRow("memory")]
    public async Task MissingRequiredFieldsAreRejectedAsProtocolFailures(string streamKind)
    {
        await using RawWebSocketServer server = RawWebSocketServer.SendText(
            "{}",
            connectionCount: 1);
        ClashStreamClient client = CreateClient(server.Endpoint, maximumProtocolFailures: 1);
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(10));

        Exception exception = streamKind switch
        {
            "connections" => await CaptureExceptionAsync(
                client.StreamConnectionsAsync(guard.Token),
                guard.Token),
            "logs" => await CaptureExceptionAsync(
                client.StreamLogsAsync(ClashLogLevel.Info, guard.Token),
                guard.Token),
            "traffic" => await CaptureExceptionAsync(
                client.StreamTrafficAsync(guard.Token),
                guard.Token),
            "memory" => await CaptureExceptionAsync(
                client.StreamMemoryAsync(guard.Token),
                guard.Token),
            _ => throw new InvalidOperationException($"Unknown stream kind: {streamKind}"),
        };

        Assert.IsInstanceOfType<ClashProtocolException>(exception);
    }

    [TestMethod]
    [DataRow("connections", "{\"connections\":null}")]
    [DataRow("connections", "{\"connections\":[{\"id\":null,\"metadata\":{}}]}")]
    [DataRow("connections", "{\"connections\":[{\"id\":\"one\",\"metadata\":null}]}")]
    [DataRow("logs", "{\"type\":null,\"payload\":\"message\"}")]
    [DataRow("logs", "{\"type\":\"  \",\"payload\":\"message\"}")]
    [DataRow("logs", "{\"type\":\"info\",\"payload\":null}")]
    [DataRow("traffic", "{\"down\":null,\"up\":1}")]
    [DataRow("memory", "{\"inuse\":null}")]
    public async Task NullOrBlankRequiredStreamFieldsAreRejectedAsProtocolFailures(
        string streamKind,
        string payload)
    {
        await using RawWebSocketServer server = RawWebSocketServer.SendText(
            payload,
            connectionCount: 1);
        ClashStreamClient client = CreateClient(server.Endpoint, maximumProtocolFailures: 1);
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(10));

        Exception exception = streamKind switch
        {
            "connections" => await CaptureExceptionAsync(
                client.StreamConnectionsAsync(guard.Token),
                guard.Token),
            "logs" => await CaptureExceptionAsync(
                client.StreamLogsAsync(ClashLogLevel.Info, guard.Token),
                guard.Token),
            "traffic" => await CaptureExceptionAsync(
                client.StreamTrafficAsync(guard.Token),
                guard.Token),
            "memory" => await CaptureExceptionAsync(
                client.StreamMemoryAsync(guard.Token),
                guard.Token),
            _ => throw new InvalidOperationException($"Unknown stream kind: {streamKind}"),
        };

        Assert.IsInstanceOfType<ClashProtocolException>(exception);
    }

    [TestMethod]
    [DataRow("{\"connections\":[],\"uploadTotal\":2}")]
    [DataRow("{\"connections\":[],\"downloadTotal\":1}")]
    [DataRow("{\"connections\":[{\"id\":\"one\",\"upload\":2,\"metadata\":{}}],\"downloadTotal\":1,\"uploadTotal\":2}")]
    [DataRow("{\"connections\":[{\"id\":\"one\",\"download\":1,\"metadata\":{}}],\"downloadTotal\":1,\"uploadTotal\":2}")]
    public async Task MissingRequiredConnectionCountersAreRejectedBeforePublishingSnapshot(
        string payload)
    {
        await using RawWebSocketServer server = RawWebSocketServer.SendText(
            payload,
            connectionCount: 1);
        ClashStreamClient client = CreateClient(server.Endpoint, maximumProtocolFailures: 1);
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(10));
        List<ConnectionStreamSnapshot> snapshots = [];

        Exception exception = await CaptureExceptionAsync(
            client.StreamConnectionsAsync(guard.Token),
            guard.Token,
            snapshots.Add);

        Assert.IsInstanceOfType<ClashProtocolException>(exception);
        Assert.IsEmpty(snapshots);
    }

    [TestMethod]
    public async Task ConsecutiveMalformedMessagesStopAtConfiguredFailureLimit()
    {
        await using RawWebSocketServer server = RawWebSocketServer.SendText("{", connectionCount: 2);
        ConcurrentQueue<ClashStreamStatus> statuses = new();
        ClashStreamClient client = CreateClient(server.Endpoint, maximumProtocolFailures: 2);
        client.StreamStatusChanged += (_, args) => statuses.Enqueue(args.Status);
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(10));

        Exception exception = await CaptureExceptionAsync(
            client.StreamTrafficAsync(guard.Token),
            guard.Token);

        Assert.IsInstanceOfType<ClashProtocolException>(exception);
        ClashStreamStatus[] trafficStatuses = statuses
            .Where(static status => status.Kind == ClashStreamKind.Traffic)
            .ToArray();
        Assert.AreEqual(2, trafficStatuses.Count(static status =>
            status.State == ClashStreamState.Connected));
        Assert.AreEqual(1, trafficStatuses.Count(static status =>
            status.State == ClashStreamState.Retrying));
        Assert.AreEqual(ClashStreamState.Faulted, trafficStatuses[^1].State);
    }

    [TestMethod]
    public async Task FragmentedMessageAtConfiguredSizeLimitIsAccepted()
    {
        string payload = CreateTrafficPayload(1024, down: 41, up: 17);
        await using RawWebSocketServer server = RawWebSocketServer.SendFragmentedText(
            payload,
            firstFragmentLength: 600);
        ClashStreamClient client = CreateClient(
            server.Endpoint,
            maximumProtocolFailures: 1,
            receiveBufferSize: 1024,
            maximumMessageSize: 1024);
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(10));

        ClashTrafficSample sample = await ReadFirstAsync(
            client.StreamTrafficAsync(guard.Token),
            guard.Token);

        Assert.AreEqual(41L, sample.Down);
        Assert.AreEqual(17L, sample.Up);
        Assert.AreEqual(1, server.AcceptedConnectionCount);
    }

    [TestMethod]
    public async Task FragmentedMessageAboveConfiguredSizeLimitStopsWithProtocolFailure()
    {
        string payload = CreateTrafficPayload(1025, down: 43, up: 19);
        await using RawWebSocketServer server = RawWebSocketServer.SendFragmentedText(
            payload,
            firstFragmentLength: 600);
        ConcurrentQueue<ClashStreamStatus> statuses = new();
        ClashStreamClient client = CreateClient(
            server.Endpoint,
            maximumProtocolFailures: 1,
            receiveBufferSize: 1024,
            maximumMessageSize: 1024);
        client.StreamStatusChanged += (_, args) => statuses.Enqueue(args.Status);
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(10));

        Exception exception = await CaptureExceptionAsync(
            client.StreamTrafficAsync(guard.Token),
            guard.Token);

        Assert.IsInstanceOfType<ClashProtocolException>(exception);
        StringAssert.Contains(exception.Message, "exceeded the configured size limit");
        ClashStreamStatus[] trafficStatuses = statuses
            .Where(static status => status.Kind == ClashStreamKind.Traffic)
            .ToArray();
        CollectionAssert.AreEqual(
            new[]
            {
                ClashStreamState.Connecting,
                ClashStreamState.Connected,
                ClashStreamState.Faulted,
            },
            trafficStatuses.Select(static status => status.State).ToArray());
        Assert.AreEqual(1, server.AcceptedConnectionCount);
    }

    [TestMethod]
    public async Task HealthyFrameAfterReconnectResetsProtocolFailureCount()
    {
        await using RawWebSocketServer server = RawWebSocketServer.SendSequence(
            ("{\"down\":1,\"up\":2}", true),
            ("{", false),
            ("{\"down\":3,\"up\":4}", true),
            ("{", false),
            ("{", false));
        ConcurrentQueue<ClashStreamStatus> statuses = new();
        ClashStreamClient client = CreateClient(server.Endpoint, maximumProtocolFailures: 2);
        client.StreamStatusChanged += (_, args) => statuses.Enqueue(args.Status);
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(10));
        await using IAsyncEnumerator<ClashTrafficSample> enumerator = client
            .StreamTrafficAsync(guard.Token)
            .GetAsyncEnumerator(guard.Token);

        Assert.IsTrue(await enumerator.MoveNextAsync());
        Assert.AreEqual(1L, enumerator.Current.Down);
        Assert.IsTrue(await enumerator.MoveNextAsync());
        Assert.AreEqual(3L, enumerator.Current.Down);
        Exception exception = await CaptureExceptionAsync(enumerator.MoveNextAsync().AsTask());

        Assert.IsInstanceOfType<ClashProtocolException>(exception);
        Assert.AreEqual(5, server.AcceptedConnectionCount);
        ClashStreamStatus[] trafficStatuses = statuses
            .Where(static status => status.Kind == ClashStreamKind.Traffic)
            .ToArray();
        CollectionAssert.Contains(
            trafficStatuses.Select(static status => status.State).ToArray(),
            ClashStreamState.Connecting);
        CollectionAssert.Contains(
            trafficStatuses.Select(static status => status.State).ToArray(),
            ClashStreamState.Connected);
        CollectionAssert.Contains(
            trafficStatuses.Select(static status => status.State).ToArray(),
            ClashStreamState.Retrying);
        Assert.AreEqual(5, trafficStatuses.Count(static status =>
            status.State == ClashStreamState.Connected));
        Assert.AreEqual(ClashStreamState.Faulted, trafficStatuses[^1].State);

        ClashStreamStatus[] cleanCloseRetries = trafficStatuses
            .Where(static status =>
                status.State == ClashStreamState.Retrying &&
                status.Detail == "The remote endpoint closed the stream.")
            .ToArray();
        Assert.HasCount(2, cleanCloseRetries);
        Assert.IsTrue(cleanCloseRetries.All(static status => status.RetryAttempt == 1));
        Assert.AreEqual(2, trafficStatuses.Count(static status =>
            status.State == ClashStreamState.Retrying &&
            status.Detail == "The stream returned invalid protocol data; retrying."));
    }

    [TestMethod]
    public async Task CallerCancellationStopsConnectedStreamWithoutFaultOrRetryStatus()
    {
        await using RawWebSocketServer server = RawWebSocketServer.AcceptAndWait();
        ConcurrentQueue<ClashStreamStatus> statuses = new();
        TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ClashStreamClient client = CreateClient(server.Endpoint, maximumProtocolFailures: 2);
        client.StreamStatusChanged += (_, args) =>
        {
            statuses.Enqueue(args.Status);
            if (args.Status.State == ClashStreamState.Connected)
            {
                connected.TrySetResult();
            }
        };
        using CancellationTokenSource cancellationSource = new();
        await using IAsyncEnumerator<ClashTrafficSample> enumerator = client
            .StreamTrafficAsync(cancellationSource.Token)
            .GetAsyncEnumerator();
        Task<bool> moveNext = enumerator.MoveNextAsync().AsTask();
        await Task.WhenAll(server.HandshakeCompleted, connected.Task)
            .WaitAsync(TimeSpan.FromSeconds(5));

        await cancellationSource.CancelAsync();
        Exception exception = await CaptureExceptionAsync(moveNext);

        Assert.IsInstanceOfType<OperationCanceledException>(exception);
        ClashStreamStatus[] trafficStatuses = statuses
            .Where(static status => status.Kind == ClashStreamKind.Traffic)
            .ToArray();
        CollectionAssert.AreEqual(
            new[] { ClashStreamState.Connecting, ClashStreamState.Connected },
            trafficStatuses.Select(static status => status.State).ToArray());
        Assert.IsFalse(trafficStatuses.Any(static status =>
            status.State is ClashStreamState.Faulted or ClashStreamState.Retrying));
    }

    private static ClashStreamClient CreateClient(
        Uri endpoint,
        int maximumProtocolFailures,
        CapabilityRegistry? capabilities = null,
        int receiveBufferSize = 16 * 1024,
        int maximumMessageSize = 16 * 1024 * 1024,
        int logCapacity = 1024)
    {
        BackendProfile profile = new(
            Guid.Parse("138e74cc-9ff0-41d4-a944-c67336091ab1"),
            "Stream test backend",
            BackendEndpoint.Create(endpoint.AbsoluteUri));
        return new ClashStreamClient(
            profile,
            new BackendCredential("stream-secret"),
            capabilities ?? new CapabilityRegistry(),
            CancellationToken.None,
            new ClashWebSocketOptions
            {
                MinimumReconnectDelay = TimeSpan.Zero,
                MaximumReconnectDelay = TimeSpan.Zero,
                MaximumConsecutiveProtocolFailures = maximumProtocolFailures,
                ReceiveBufferSize = receiveBufferSize,
                MaximumMessageSize = maximumMessageSize,
                LogCapacity = logCapacity,
            });
    }

    private static RawWebSocketServer CreateGatedLogOverflowServer() =>
        RawWebSocketServer.SendGatedSequence(
            ("{\"type\":\"info\",\"payload\":\"one\"}", true),
            ("{\"type\":\"info\",\"payload\":\"two\"}", true),
            ("{\"type\":\"info\",\"payload\":\"three\"}", true),
            ("{\"type\":\"info\",\"payload\":\"four\"}", true),
            ("{", false));

    private static string CreateTrafficPayload(int utf8ByteLength, long down, long up)
    {
        string prefix = $"{{\"down\":{down},\"up\":{up},\"padding\":\"";
        const string Suffix = "\"}";
        int paddingLength = utf8ByteLength - Encoding.UTF8.GetByteCount(prefix + Suffix);
        Assert.IsGreaterThanOrEqualTo(0, paddingLength);

        string payload = prefix + new string('a', paddingLength) + Suffix;
        Assert.AreEqual(utf8ByteLength, Encoding.UTF8.GetByteCount(payload));
        return payload;
    }

    private static async Task<T> ReadFirstAsync<T>(
        IAsyncEnumerable<T> stream,
        CancellationToken cancellationToken)
    {
        await using IAsyncEnumerator<T> enumerator = stream.GetAsyncEnumerator(cancellationToken);
        Assert.IsTrue(await enumerator.MoveNextAsync());
        return enumerator.Current;
    }

    private static async Task AssertRequestAsync(
        RawWebSocketServer server,
        string expectedPath,
        params string[] expectedQueryParts)
    {
        string headers = await server.RequestHeaders.WaitAsync(TimeSpan.FromSeconds(5));
        string requestLine = headers.Split("\r\n", StringSplitOptions.None)[0];
        Assert.IsTrue(
            requestLine.StartsWith($"GET {expectedPath}?", StringComparison.Ordinal),
            $"Unexpected WebSocket request line: {requestLine}");
        Assert.IsTrue(
            requestLine.EndsWith(" HTTP/1.1", StringComparison.Ordinal),
            $"Unexpected WebSocket request line: {requestLine}");
        foreach (string queryPart in expectedQueryParts)
        {
            Assert.IsTrue(
                requestLine.Contains(queryPart, StringComparison.Ordinal),
                $"The WebSocket request line did not contain '{queryPart}': {requestLine}");
        }
    }

    private static async Task<Exception> CaptureExceptionAsync<T>(
        IAsyncEnumerable<T> stream,
        CancellationToken cancellationToken,
        Action<T>? observeItem = null)
    {
        try
        {
            await foreach (T item in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                observeItem?.Invoke(item);
            }
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new AssertFailedException("The stream completed without its terminal exception.");
    }

    private static async Task<Exception> CaptureExceptionAsync(Task operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new AssertFailedException("The operation completed without an exception.");
    }

    private sealed class RawWebSocketServer : IAsyncDisposable
    {
        private const string WebSocketMagic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _lifetimeSource = new();
        private readonly TaskCompletionSource _handshakeCompleted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> _requestHeaders = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _remainingConnectionsReleased = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _serverTask;
        private int _acceptedConnectionCount;

        private RawWebSocketServer(
            HttpStatusCode? rejectionStatus,
            string? payload,
            int connectionCount,
            bool waitAfterHandshake = false,
            IReadOnlyList<ConnectionScript>? connectionScripts = null,
            bool waitForRemainingConnections = false)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Endpoint = new Uri($"http://127.0.0.1:{port}/api/");
            _serverTask = ServeAsync(
                rejectionStatus,
                payload,
                connectionCount,
                waitAfterHandshake,
                connectionScripts,
                waitForRemainingConnections,
                _lifetimeSource.Token);
        }

        public Uri Endpoint { get; }

        public int AcceptedConnectionCount => Volatile.Read(ref _acceptedConnectionCount);

        public Task HandshakeCompleted => _handshakeCompleted.Task;

        public Task<string> RequestHeaders => _requestHeaders.Task;

        public static RawWebSocketServer Reject(HttpStatusCode statusCode) =>
            new(statusCode, payload: null, connectionCount: 1);

        public static RawWebSocketServer SendText(string payload, int connectionCount) =>
            new(rejectionStatus: null, payload, connectionCount);

        public static RawWebSocketServer SendFragmentedText(
            string payload,
            int firstFragmentLength) =>
            new(
                rejectionStatus: null,
                payload: null,
                connectionCount: 1,
                connectionScripts:
                [
                    new ConnectionScript(
                        payload,
                        firstFragmentLength,
                        SendCleanClose: false),
                ]);

        public static RawWebSocketServer SendSequence(
            params (string Payload, bool SendCleanClose)[] messages) =>
            new(
                rejectionStatus: null,
                payload: null,
                connectionCount: messages.Length,
                connectionScripts: messages
                    .Select(static message => new ConnectionScript(
                        message.Payload,
                        FirstFragmentLength: null,
                        message.SendCleanClose))
                    .ToArray());

        public static RawWebSocketServer SendGatedSequence(
            params (string Payload, bool SendCleanClose)[] messages) =>
            new(
                rejectionStatus: null,
                payload: null,
                connectionCount: messages.Length,
                connectionScripts: messages
                    .Select(static message => new ConnectionScript(
                        message.Payload,
                        FirstFragmentLength: null,
                        message.SendCleanClose))
                    .ToArray(),
                waitForRemainingConnections: true);

        public static RawWebSocketServer AcceptAndWait() =>
            new(rejectionStatus: null, payload: null, connectionCount: 1, waitAfterHandshake: true);

        public void ReleaseRemainingConnections() =>
            _remainingConnectionsReleased.TrySetResult();

        private async Task ServeAsync(
            HttpStatusCode? rejectionStatus,
            string? payload,
            int connectionCount,
            bool waitAfterHandshake,
            IReadOnlyList<ConnectionScript>? connectionScripts,
            bool waitForRemainingConnections,
            CancellationToken cancellationToken)
        {
            int effectiveConnectionCount = connectionScripts?.Count ?? connectionCount;
            for (int index = 0; index < effectiveConnectionCount; index++)
            {
                if (index == 1 && waitForRemainingConnections)
                {
                    await _remainingConnectionsReleased.Task.WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                }

                using TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken)
                    .ConfigureAwait(false);
                Interlocked.Increment(ref _acceptedConnectionCount);
                await using NetworkStream stream = client.GetStream();
                string requestHeaders = await ReadHeadersAsync(stream, cancellationToken)
                    .ConfigureAwait(false);
                _requestHeaders.TrySetResult(requestHeaders);

                if (rejectionStatus.HasValue)
                {
                    string response =
                        $"HTTP/1.1 {(int)rejectionStatus.Value} Rejected\r\n" +
                        "Connection: close\r\nContent-Length: 0\r\n\r\n";
                    await stream.WriteAsync(
                        Encoding.ASCII.GetBytes(response),
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                string key = GetHeader(requestHeaders, "Sec-WebSocket-Key");
#pragma warning disable CA5350 // RFC 6455 requires SHA-1 for the WebSocket accept value.
                string accept = Convert.ToBase64String(
                    SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketMagic)));
#pragma warning restore CA5350
                string handshake =
                    "HTTP/1.1 101 Switching Protocols\r\n" +
                    "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
                    $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
                await stream.WriteAsync(
                    Encoding.ASCII.GetBytes(handshake),
                    cancellationToken).ConfigureAwait(false);
                _handshakeCompleted.TrySetResult();
                if (waitAfterHandshake)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                    return;
                }

                ConnectionScript? script = connectionScripts?[index];
                string currentPayload = script?.Payload ?? payload ?? string.Empty;
                if (script?.FirstFragmentLength is int firstFragmentLength)
                {
                    await WriteFragmentedTextAsync(
                        stream,
                        currentPayload,
                        firstFragmentLength,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await WriteTextFrameAsync(stream, currentPayload, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (script?.SendCleanClose == true)
                {
                    await WriteFrameAsync(
                        stream,
                        opcode: 0x08,
                        endOfMessage: true,
                        ReadOnlyMemory<byte>.Empty,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private static async Task<string> ReadHeadersAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[16 * 1024];
            int length = 0;
            while (length < buffer.Length)
            {
                int read = await stream.ReadAsync(
                    buffer.AsMemory(length, 1),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                length += read;
                if (length >= 4 &&
                    buffer[length - 4] == '\r' &&
                    buffer[length - 3] == '\n' &&
                    buffer[length - 2] == '\r' &&
                    buffer[length - 1] == '\n')
                {
                    return Encoding.ASCII.GetString(buffer, 0, length);
                }
            }

            throw new InvalidDataException("The WebSocket client did not send complete HTTP headers.");
        }

        private static string GetHeader(string headers, string name)
        {
            string prefix = $"{name}:";
            string? header = headers
                .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            return header is null
                ? throw new InvalidDataException($"The WebSocket request did not include {name}.")
                : header[prefix.Length..].Trim();
        }

        private static async Task WriteTextFrameAsync(
            NetworkStream stream,
            string value,
            CancellationToken cancellationToken)
        {
            byte[] payload = Encoding.UTF8.GetBytes(value);
            await WriteFrameAsync(
                stream,
                opcode: 0x01,
                endOfMessage: true,
                payload,
                cancellationToken).ConfigureAwait(false);
        }

        private static async Task WriteFragmentedTextAsync(
            NetworkStream stream,
            string value,
            int firstFragmentLength,
            CancellationToken cancellationToken)
        {
            byte[] payload = Encoding.UTF8.GetBytes(value);
            if (firstFragmentLength <= 0 || firstFragmentLength >= payload.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(firstFragmentLength));
            }

            await WriteFrameAsync(
                stream,
                opcode: 0x01,
                endOfMessage: false,
                payload.AsMemory(0, firstFragmentLength),
                cancellationToken).ConfigureAwait(false);
            await WriteFrameAsync(
                stream,
                opcode: 0x00,
                endOfMessage: true,
                payload.AsMemory(firstFragmentLength),
                cancellationToken).ConfigureAwait(false);
        }

        private static async Task WriteFrameAsync(
            NetworkStream stream,
            byte opcode,
            bool endOfMessage,
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken)
        {
            if (payload.Length > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(payload));
            }

            int headerLength = payload.Length < 126 ? 2 : 4;
            byte[] frame = new byte[payload.Length + headerLength];
            frame[0] = checked((byte)((endOfMessage ? 0x80 : 0x00) | opcode));
            if (payload.Length < 126)
            {
                frame[1] = checked((byte)payload.Length);
            }
            else
            {
                frame[1] = 126;
                frame[2] = checked((byte)(payload.Length >> 8));
                frame[3] = checked((byte)(payload.Length & 0xff));
            }

            payload.CopyTo(frame.AsMemory(headerLength));
            await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private sealed record ConnectionScript(
            string Payload,
            int? FirstFragmentLength,
            bool SendCleanClose);

        public async ValueTask DisposeAsync()
        {
            await _lifetimeSource.CancelAsync().ConfigureAwait(false);
            _listener.Stop();
            try
            {
                await _serverTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetimeSource.IsCancellationRequested)
            {
            }
            finally
            {
                _lifetimeSource.Dispose();
            }
        }
    }
}
