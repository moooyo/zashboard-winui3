using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Zashboard.Core.Backends;
using Zashboard.Core.Capabilities;
using Zashboard.Core.Clash;
using Zashboard.Infrastructure.Clash;

namespace Zashboard.Infrastructure.Tests;

[TestClass]
public sealed class ClashConnectionContractTests
{
    [TestMethod]
    [DataRow("[\"CN\",\"PRIVATE\"]", "[\"US\",\"CA\"]", "CN|PRIVATE", "US|CA")]
    [DataRow("[]", "[]", "", "")]
    [DataRow("null", "null", "", "")]
    [DataRow("\"CN\"", "\"US\"", "CN", "US")]
    [DataRow("null", "[\"US\"]", "", "US")]
    [DataRow("[\"CN\"]", "\"US\"", "CN", "US")]
    public async Task GeoIpContractSupportsMihomoArraysAndLegacyStrings(
        string sourceGeoIpJson,
        string destinationGeoIpJson,
        string expectedSource,
        string expectedDestination)
    {
        // Reduced from the v1.19.12 Metadata and Snapshot JSON contracts:
        // https://github.com/MetaCubeX/mihomo/blob/v1.19.12/constant/metadata.go
        // https://github.com/MetaCubeX/mihomo/blob/v1.19.12/tunnel/statistic/manager.go
        string payload = $$"""
            {
              "downloadTotal": 100,
              "uploadTotal": 200,
              "connections": [{
                "id": "connection-1",
                "metadata": {
                  "network": "tcp",
                  "type": "Socks5",
                  "sourceIP": "192.0.2.1",
                  "destinationIP": "203.0.113.1",
                  "sourceGeoIP": {{sourceGeoIpJson}},
                  "destinationGeoIP": {{destinationGeoIpJson}},
                  "sourcePort": "49152",
                  "destinationPort": "443",
                  "host": "example.com"
                },
                "upload": 20,
                "download": 10,
                "start": "2026-08-30T04:05:06Z",
                "chains": ["DIRECT"],
                "rule": "Match",
                "rulePayload": ""
              }],
              "memory": 300
            }
            """;

        ConnectionStreamSnapshot snapshot = await ReadSnapshotAsync(payload);

        ClashConnection connection = snapshot.Connections.Single();
        CollectionAssert.AreEqual(
            expectedSource.Split('|', StringSplitOptions.RemoveEmptyEntries),
            connection.Metadata.SourceGeoIp.ToArray());
        CollectionAssert.AreEqual(
            expectedDestination.Split('|', StringSplitOptions.RemoveEmptyEntries),
            connection.Metadata.DestinationGeoIp.ToArray());
        Assert.AreEqual("example.com", connection.Metadata.Host);
        Assert.AreEqual("443", connection.Metadata.DestinationPort);
        Assert.AreEqual(10L, connection.Download);
        Assert.AreEqual(20L, connection.Upload);
        Assert.AreEqual(100L, snapshot.DownloadTotal);
        Assert.AreEqual(200L, snapshot.UploadTotal);
        Assert.AreEqual(300L, snapshot.Memory);
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("[]")]
    public async Task EmptyConnectionSnapshotPreservesTotals(string connectionsJson)
    {
        ConnectionStreamSnapshot snapshot = await ReadSnapshotAsync(
            $$"""{"downloadTotal":100,"uploadTotal":200,"connections":{{connectionsJson}},"memory":300}""");

        Assert.IsEmpty(snapshot.Connections);
        Assert.AreEqual(100L, snapshot.DownloadTotal);
        Assert.AreEqual(200L, snapshot.UploadTotal);
        Assert.AreEqual(300L, snapshot.Memory);
    }

    [TestMethod]
    public async Task OmittedOptionalGeoIpFieldsMapToEmptyCollections()
    {
        ConnectionStreamSnapshot snapshot = await ReadSnapshotAsync(
            """{"connections":[{"id":"one","download":1,"upload":2,"metadata":{}}],"downloadTotal":1,"uploadTotal":2}""");

        Assert.IsEmpty(snapshot.Connections.Single().Metadata.SourceGeoIp);
        Assert.IsEmpty(snapshot.Connections.Single().Metadata.DestinationGeoIp);
    }

    [TestMethod]
    [DataRow("{\"downloadTotal\":1,\"uploadTotal\":2}")]
    [DataRow("{\"connections\":null,\"uploadTotal\":2}")]
    [DataRow("{\"connections\":null,\"downloadTotal\":1}")]
    [DataRow("{\"connections\":[null],\"downloadTotal\":1,\"uploadTotal\":2}")]
    [DataRow("{\"connections\":[{\"id\":null,\"download\":1,\"upload\":2,\"metadata\":{}}],\"downloadTotal\":1,\"uploadTotal\":2}")]
    [DataRow("{\"connections\":[{\"id\":\"one\",\"download\":1,\"upload\":2,\"metadata\":null}],\"downloadTotal\":1,\"uploadTotal\":2}")]
    [DataRow("{\"connections\":[{\"id\":\"one\",\"download\":1,\"metadata\":{}}],\"downloadTotal\":1,\"uploadTotal\":2}")]
    [DataRow("{\"connections\":[{\"id\":\"one\",\"upload\":2,\"metadata\":{}}],\"downloadTotal\":1,\"uploadTotal\":2}")]
    public async Task EmptySnapshotCompatibilityStillRejectsInvalidRequiredFields(string payload)
    {
        await Assert.ThrowsExactlyAsync<ClashProtocolException>(() => ReadSnapshotAsync(payload));
    }

    [TestMethod]
    [DataRow("sourceGeoIP", "[null]")]
    [DataRow("sourceGeoIP", "[1]")]
    [DataRow("sourceGeoIP", "{}")]
    [DataRow("sourceGeoIP", "true")]
    [DataRow("destinationGeoIP", "[null]")]
    [DataRow("destinationGeoIP", "[[\"US\"]]")]
    [DataRow("destinationGeoIP", "[\"US\",false]")]
    [DataRow("destinationGeoIP", "42")]
    public async Task InvalidGeoIpShapesAreRejectedAsProtocolFailures(
        string propertyName,
        string geoIpJson)
    {
        string payload = $$"""
            {
              "connections": [{
                "id": "one",
                "download": 1,
                "upload": 2,
                "metadata": {"{{propertyName}}": {{geoIpJson}}}
              }],
              "downloadTotal": 1,
              "uploadTotal": 2
            }
            """;

        await Assert.ThrowsExactlyAsync<ClashProtocolException>(() => ReadSnapshotAsync(payload));
    }

    private static async Task<ConnectionStreamSnapshot> ReadSnapshotAsync(string payload)
    {
        await using ConnectionFixtureServer server = new(payload);
        BackendProfile profile = new(
            Guid.NewGuid(),
            "Connection contract fixture",
            BackendEndpoint.Create(server.Endpoint.AbsoluteUri));
        ClashStreamClient client = new(
            profile,
            new BackendCredential(null),
            new CapabilityRegistry(),
            CancellationToken.None,
            new ClashWebSocketOptions { MaximumConsecutiveProtocolFailures = 1 });
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(10));
        await using IAsyncEnumerator<ConnectionStreamSnapshot> enumerator = client
            .StreamConnectionsAsync(guard.Token)
            .GetAsyncEnumerator(guard.Token);

        Assert.IsTrue(await enumerator.MoveNextAsync().AsTask().WaitAsync(guard.Token));
        return enumerator.Current;
    }

    private sealed class ConnectionFixtureServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _lifetimeSource = new();
        private readonly Task _serverTask;

        public ConnectionFixtureServer(string payload)
        {
            _listener.Start();
            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Endpoint = new Uri($"http://127.0.0.1:{port}/");
            _serverTask = ServeAsync(payload, _lifetimeSource.Token);
        }

        public Uri Endpoint { get; }

        private async Task ServeAsync(string payload, CancellationToken cancellationToken)
        {
            using TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken);
            await using NetworkStream stream = client.GetStream();
            using StreamReader reader = new(stream, Encoding.ASCII, leaveOpen: true);
            string? key = null;
            while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 } line)
            {
                const string KeyHeader = "Sec-WebSocket-Key:";
                if (line.StartsWith(KeyHeader, StringComparison.OrdinalIgnoreCase))
                {
                    key = line[KeyHeader.Length..].Trim();
                }
            }

            Assert.IsNotNull(key);
#pragma warning disable CA5350 // RFC 6455 requires SHA-1 for the WebSocket accept value.
            string accept = Convert.ToBase64String(SHA1.HashData(
                Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
#pragma warning restore CA5350
            string handshake =
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
                $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(handshake), cancellationToken);

            using WebSocket socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
            {
                IsServer = true,
                KeepAliveInterval = Timeout.InfiniteTimeSpan,
            });
            await socket.SendAsync(
                Encoding.UTF8.GetBytes(payload).AsMemory(),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await _lifetimeSource.CancelAsync();
            _listener.Stop();
            try
            {
                await _serverTask;
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
