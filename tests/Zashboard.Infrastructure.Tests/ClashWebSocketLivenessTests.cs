using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Zashboard.Core.Backends;
using Zashboard.Core.Capabilities;
using Zashboard.Core.Clash;
using Zashboard.Infrastructure.Clash;

namespace Zashboard.Infrastructure.Tests;

[TestClass]
public sealed class ClashWebSocketLivenessTests
{
    private static readonly TimeSpan MessageTimeout = TimeSpan.FromMilliseconds(300);

    [TestMethod]
    public async Task MissingHandshakeResponseRetriesAndRecovers()
    {
        await using ScriptedServer server = new(
            new ConnectionScript(CompleteHandshake: false, []),
            Samples(ClashStreamKind.Traffic, 42));
        ConcurrentQueue<ClashStreamStatus> statuses = new();
        ClashStreamClient client = CreateClient(server, statuses);
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(10));

        await using IAsyncEnumerator<long> reader = ReadValuesAsync(client, ClashStreamKind.Traffic, guard.Token)
            .GetAsyncEnumerator(guard.Token);
        Assert.IsTrue(await reader.MoveNextAsync());

        Assert.AreEqual(42L, reader.Current);
        AssertTimeoutRecovery(server, statuses, "handshake");
    }

    [TestMethod]
    [DataRow(ClashStreamKind.Connections)]
    [DataRow(ClashStreamKind.Traffic)]
    [DataRow(ClashStreamKind.Memory)]
    public async Task PeriodicStreamWithoutFirstSampleRetriesAndRecovers(ClashStreamKind kind)
    {
        await using ScriptedServer server = new(
            new ConnectionScript(CompleteHandshake: true, []),
            Samples(kind, 42));
        ConcurrentQueue<ClashStreamStatus> statuses = new();
        ClashStreamClient client = CreateClient(server, statuses);
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(10));

        await using IAsyncEnumerator<long> reader = ReadValuesAsync(client, kind, guard.Token)
            .GetAsyncEnumerator(guard.Token);
        Assert.IsTrue(await reader.MoveNextAsync());

        Assert.AreEqual(42L, reader.Current);
        AssertTimeoutRecovery(server, statuses, "message");
    }

    [TestMethod]
    public async Task PeriodicStreamThatStopsAfterHealthySampleRetriesAndRecovers()
    {
        await using ScriptedServer server = new(
            Samples(ClashStreamKind.Traffic, 10),
            Samples(ClashStreamKind.Traffic, 42));
        ConcurrentQueue<ClashStreamStatus> statuses = new();
        ClashStreamClient client = CreateClient(server, statuses);
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(10));

        await using IAsyncEnumerator<long> reader = ReadValuesAsync(client, ClashStreamKind.Traffic, guard.Token)
            .GetAsyncEnumerator(guard.Token);
        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.AreEqual(10L, reader.Current);
        Assert.IsTrue(await reader.MoveNextAsync());

        Assert.AreEqual(42L, reader.Current);
        AssertTimeoutRecovery(server, statuses, "message");
    }

    [TestMethod]
    public async Task PeriodicSamplesRenewDeadlineAfterEveryCompleteMessage()
    {
        FrameScript[] frames = Enumerable.Range(1, 8)
            .Select(index => new FrameScript(
                TrafficPayload(index),
                Delay: TimeSpan.FromMilliseconds(100)))
            .ToArray();
        await using ScriptedServer server = new(new ConnectionScript(true, frames));
        ConcurrentQueue<ClashStreamStatus> statuses = new();
        ClashStreamClient client = CreateClient(server, statuses);
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(10));

        await using IAsyncEnumerator<long> reader = ReadValuesAsync(client, ClashStreamKind.Traffic, guard.Token)
            .GetAsyncEnumerator(guard.Token);
        for (long expected = 1; expected <= 8; expected++)
        {
            Assert.IsTrue(await reader.MoveNextAsync());
            Assert.AreEqual(expected, reader.Current);
        }

        Assert.AreEqual(1, server.AcceptedConnectionCount);
        Assert.IsFalse(statuses.Any(status => status.State is ClashStreamState.Retrying or ClashStreamState.Faulted));
    }

    [TestMethod]
    public async Task IdleLogStreamRemainsConnectedBeyondMessageAndHandshakeDeadlines()
    {
        await using ScriptedServer server = new(new ConnectionScript(true,
        [
            new FrameScript("{\"type\":\"info\",\"payload\":\"after idle\"}", Delay: TimeSpan.FromMilliseconds(1200)),
        ]));
        ConcurrentQueue<ClashStreamStatus> statuses = new();
        ClashStreamClient client = CreateClient(server, statuses);
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(10));

        await using IAsyncEnumerator<ClashLogMessage> reader = client.StreamLogsAsync(ClashLogLevel.Info, guard.Token)
            .GetAsyncEnumerator(guard.Token);
        Assert.IsTrue(await reader.MoveNextAsync());

        Assert.AreEqual("after idle", reader.Current.Payload);
        Assert.AreEqual(1, server.AcceptedConnectionCount);
        Assert.IsFalse(statuses.Any(status => status.State is ClashStreamState.Retrying or ClashStreamState.Faulted));
    }

    [TestMethod]
    [DataRow(ClashStreamKind.Traffic)]
    [DataRow(ClashStreamKind.Logs)]
    public async Task UnfinishedFragmentedMessageRetriesDespiteContinuedFragments(ClashStreamKind kind)
    {
        FrameScript[] fragments = Enumerable.Range(0, 10)
            .Select(index => new FrameScript(
                " ",
                EndOfMessage: false,
                Opcode: index == 0 ? (byte)0x01 : (byte)0x00,
                Delay: TimeSpan.FromMilliseconds(100)))
            .ToArray();
        await using ScriptedServer server = new(
            new ConnectionScript(true, fragments),
            Samples(kind, 42));
        ConcurrentQueue<ClashStreamStatus> statuses = new();
        ClashStreamClient client = CreateClient(server, statuses);
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(10));

        await using IAsyncEnumerator<long> reader = ReadValuesAsync(client, kind, guard.Token)
            .GetAsyncEnumerator(guard.Token);
        Assert.IsTrue(await reader.MoveNextAsync());

        Assert.AreEqual(42L, reader.Current);
        AssertTimeoutRecovery(server, statuses, "message");
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task CancellationDuringHandshakeOrReceiveStopsWithoutRetryOrFault(
        bool completeHandshake,
        bool cancelSession)
    {
        await using ScriptedServer server = new(new ConnectionScript(completeHandshake, []));
        ConcurrentQueue<ClashStreamStatus> statuses = new();
        using CancellationTokenSource lifetime = new();
        TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ClashStreamClient client = CreateClient(server, statuses, cancelSession ? lifetime.Token : default);
        client.StreamStatusChanged += (_, args) =>
        {
            if (args.Status.State == ClashStreamState.Connected)
            {
                connected.TrySetResult();
            }
        };
        await using IAsyncEnumerator<ClashTrafficSample> reader = client.StreamTrafficAsync(
            cancelSession ? default : lifetime.Token).GetAsyncEnumerator();
        Task<bool> pending = reader.MoveNextAsync().AsTask();
        await (completeHandshake ? connected.Task : server.RequestReceived).WaitAsync(TimeSpan.FromSeconds(5));

        await lifetime.CancelAsync();
        _ = await Assert.ThrowsAsync<OperationCanceledException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.AreEqual(1, server.AcceptedConnectionCount);
        Assert.IsFalse(statuses.Any(status => status.State is ClashStreamState.Retrying or ClashStreamState.Faulted));
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(-1L)]
    [DataRow(long.MaxValue)]
    public void InvalidDeadlinesAreRejectedBeforeConnecting(long ticks)
    {
        TimeSpan timeout = TimeSpan.FromTicks(ticks);
        BackendProfile profile = CreateProfile(new Uri("http://127.0.0.1:1/"));
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ClashStreamClient(
            profile, new BackendCredential(""), new CapabilityRegistry(), default,
            new ClashWebSocketOptions { HandshakeTimeout = timeout }));
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ClashStreamClient(
            profile, new BackendCredential(""), new CapabilityRegistry(), default,
            new ClashWebSocketOptions { MessageTimeout = timeout }));
    }

    private static ClashStreamClient CreateClient(
        ScriptedServer server,
        ConcurrentQueue<ClashStreamStatus> statuses,
        CancellationToken sessionLifetime = default)
    {
        ClashStreamClient client = new(
            CreateProfile(server.Endpoint),
            new BackendCredential("stream-secret"),
            new CapabilityRegistry(),
            sessionLifetime,
            new ClashWebSocketOptions
            {
                HandshakeTimeout = MessageTimeout,
                MessageTimeout = MessageTimeout,
                MinimumReconnectDelay = TimeSpan.FromMilliseconds(25),
                MaximumReconnectDelay = TimeSpan.FromMilliseconds(25),
                MaximumConsecutiveProtocolFailures = 1,
            });
        client.StreamStatusChanged += (_, args) => statuses.Enqueue(args.Status);
        return client;
    }

    private static BackendProfile CreateProfile(Uri endpoint) => new(
        Guid.NewGuid(), "Liveness test backend", BackendEndpoint.Create(endpoint.AbsoluteUri));

    private static void AssertTimeoutRecovery(
        ScriptedServer server,
        IEnumerable<ClashStreamStatus> statuses,
        string operation)
    {
        Assert.AreEqual(2, server.AcceptedConnectionCount);
        Assert.IsTrue(statuses.Any(status => status.State == ClashStreamState.Retrying &&
            status.Detail == $"The WebSocket {operation} timed out; retrying."),
            string.Join(Environment.NewLine, statuses));
        Assert.IsFalse(statuses.Any(status => status.State == ClashStreamState.Faulted));
    }

    private static async IAsyncEnumerable<long> ReadValuesAsync(
        ClashStreamClient client,
        ClashStreamKind kind,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        switch (kind)
        {
            case ClashStreamKind.Connections:
                await foreach (ConnectionStreamSnapshot sample in client.StreamConnectionsAsync(cancellationToken))
                {
                    yield return sample.DownloadTotal;
                }
                break;
            case ClashStreamKind.Memory:
                await foreach (ClashMemorySample sample in client.StreamMemoryAsync(cancellationToken))
                {
                    yield return sample.InUse;
                }
                break;
            case ClashStreamKind.Logs:
                await foreach (ClashLogMessage sample in client.StreamLogsAsync(ClashLogLevel.Info, cancellationToken))
                {
                    yield return long.Parse(sample.Payload, System.Globalization.CultureInfo.InvariantCulture);
                }
                break;
            default:
                await foreach (ClashTrafficSample sample in client.StreamTrafficAsync(cancellationToken))
                {
                    yield return sample.Down;
                }
                break;
        }
    }

    private static ConnectionScript Samples(ClashStreamKind kind, long value) => new(true,
    [
        new FrameScript(kind switch
        {
            ClashStreamKind.Connections => $"{{\"connections\":[],\"downloadTotal\":{value},\"uploadTotal\":0}}",
            ClashStreamKind.Memory => $"{{\"inuse\":{value}}}",
            ClashStreamKind.Logs => $"{{\"type\":\"info\",\"payload\":\"{value}\"}}",
            _ => TrafficPayload(value),
        }),
    ]);

    private static string TrafficPayload(long value) => $"{{\"down\":{value},\"up\":0}}";

    private sealed record FrameScript(
        string Payload,
        bool EndOfMessage = true,
        byte Opcode = 0x01,
        TimeSpan Delay = default);

    private sealed record ConnectionScript(bool CompleteHandshake, IReadOnlyList<FrameScript> Frames);

    private sealed class ScriptedServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _lifetime = new();
        private readonly TaskCompletionSource _requestReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _serverTask;
        private int _acceptedConnectionCount;

        public ScriptedServer(params ConnectionScript[] scripts)
        {
            _listener.Start();
            Endpoint = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
            _serverTask = ServeAsync(scripts, _lifetime.Token);
        }

        public Uri Endpoint { get; }

        public int AcceptedConnectionCount => Volatile.Read(ref _acceptedConnectionCount);

        public Task RequestReceived => _requestReceived.Task;

        private async Task ServeAsync(IEnumerable<ConnectionScript> scripts, CancellationToken cancellationToken)
        {
            foreach (ConnectionScript script in scripts)
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken);
                Interlocked.Increment(ref _acceptedConnectionCount);
                await using NetworkStream stream = client.GetStream();
                string headers = await ReadHeadersAsync(stream, cancellationToken);
                _requestReceived.TrySetResult();
                try
                {
                    if (script.CompleteHandshake)
                    {
                        await AcceptWebSocketAsync(stream, headers, cancellationToken);
                        foreach (FrameScript frame in script.Frames)
                        {
                            if (frame.Delay > TimeSpan.Zero)
                            {
                                await Task.Delay(frame.Delay, cancellationToken);
                            }
                            await WriteFrameAsync(stream, frame, cancellationToken);
                        }
                    }

                    // The client aborts its socket when a deadline or the caller cancels a receive.
                    byte[] buffer = new byte[128];
                    while (await stream.ReadAsync(buffer, cancellationToken) > 0)
                    {
                    }
                }
                catch (IOException)
                {
                    // An aborted client socket may close with a reset instead of a FIN.
                }
            }
        }

        private static async Task<string> ReadHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[16 * 1024];
            int length = 0;
            while (length < buffer.Length)
            {
                int count = await stream.ReadAsync(buffer.AsMemory(length, 1), cancellationToken);
                if (count == 0)
                {
                    break;
                }
                length += count;
                if (length >= 4 && buffer[length - 4] == '\r' && buffer[length - 3] == '\n' &&
                    buffer[length - 2] == '\r' && buffer[length - 1] == '\n')
                {
                    return Encoding.ASCII.GetString(buffer, 0, length);
                }
            }
            throw new InvalidDataException("The WebSocket request did not contain complete HTTP headers.");
        }

        private static async Task AcceptWebSocketAsync(
            NetworkStream stream, string headers, CancellationToken cancellationToken)
        {
            const string Prefix = "Sec-WebSocket-Key:";
            string key = headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                .Single(line => line.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))[Prefix.Length..].Trim();
#pragma warning disable CA5350 // RFC 6455 requires SHA-1 for the WebSocket accept value.
            string accept = Convert.ToBase64String(SHA1.HashData(
                Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
#pragma warning restore CA5350
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                $"Sec-WebSocket-Accept: {accept}\r\n\r\n"), cancellationToken);
        }

        private static async Task WriteFrameAsync(
            NetworkStream stream, FrameScript script, CancellationToken cancellationToken)
        {
            byte[] payload = Encoding.UTF8.GetBytes(script.Payload);
            Assert.IsLessThan(126, payload.Length);
            byte[] frame = new byte[payload.Length + 2];
            frame[0] = checked((byte)((script.EndOfMessage ? 0x80 : 0x00) | script.Opcode));
            frame[1] = checked((byte)payload.Length);
            payload.CopyTo(frame, 2);
            await stream.WriteAsync(frame, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await _lifetime.CancelAsync();
            _listener.Stop();
            try
            {
                await _serverTask;
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            finally
            {
                _lifetime.Dispose();
            }
        }
    }
}
