using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Zashboard.Core.Abstractions;
using Zashboard.Core.Backends;
using Zashboard.Core.Capabilities;
using Zashboard.Core.Clash;
using Zashboard.Core.Sessions;
using Zashboard.Infrastructure;
using Zashboard.Infrastructure.Clash;
using Zashboard.Infrastructure.Persistence;

namespace Zashboard.NativeAot.Smoke;

internal static class Program
{
    private const string NativeAotMarker = "ZASHBOARD_NATIVE_AOT_SMOKE_OK";
    private const string JitMarker = "ZASHBOARD_JIT_SMOKE_OK";
    private const string RequireNativeAotVariable = "ZASHBOARD_REQUIRE_NATIVE_AOT";
    private const string SmokeSecret = "native-aot-smoke-secret";

    public static async Task<int> Main()
    {
        bool requireNativeAot = string.Equals(
            Environment.GetEnvironmentVariable(RequireNativeAotVariable),
            "1",
            StringComparison.Ordinal);
        if (requireNativeAot)
        {
            SmokeAssert.True(
                RuntimeInformation.ProcessArchitecture == Architecture.X64,
                "The Native AOT smoke must run as an x64 process.");
            SmokeAssert.True(
                !RuntimeFeature.IsDynamicCodeSupported && !RuntimeFeature.IsDynamicCodeCompiled,
                "Dynamic code must be unavailable in the Native AOT smoke process.");
        }

        string rootDirectory = Path.Combine(
            Path.GetTempPath(),
            $"Zashboard.NativeAot.Smoke.{Guid.NewGuid():N}");
        try
        {
            await VerifyDependencyInjectionAndPersistenceAsync(rootDirectory).ConfigureAwait(false);
            await VerifyClashClientAsync().ConfigureAwait(false);
            await VerifyClashStreamAsync().ConfigureAwait(false);
            await VerifyConnectionStreamsAsync().ConfigureAwait(false);
            Console.WriteLine(requireNativeAot ? NativeAotMarker : JitMarker);
            return 0;
        }
        finally
        {
            if (Directory.Exists(rootDirectory))
            {
                Directory.Delete(rootDirectory, recursive: true);
            }
        }
    }

    private static async Task VerifyDependencyInjectionAndPersistenceAsync(string rootDirectory)
    {
        ServiceCollection services = new();
        services.AddZashboardInfrastructure(new InfrastructureStorageOptions(rootDirectory));
        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        IBackendProfileStore profileStore = provider.GetRequiredService<IBackendProfileStore>();
        IBackendCredentialStore credentialStore = provider.GetRequiredService<IBackendCredentialStore>();
        _ = provider.GetRequiredService<IBackendProbe>();
        _ = provider.GetRequiredService<IBackendSessionFactory>();
        _ = provider.GetRequiredService<IHttpClientFactory>();

        Guid profileId = Guid.NewGuid();
        BackendProfile profile = new(
            profileId,
            "Native AOT smoke",
            BackendEndpoint.Create("https://controller.example/api/v1/"),
            disableCoreUpgrade: true,
            disableTunMode: true);
        await profileStore.SaveAsync(new BackendProfileSet
        {
            Profiles = [profile],
            ActiveProfileId = profileId,
        }).ConfigureAwait(false);

        BackendProfileSet loaded = await profileStore.LoadAsync().ConfigureAwait(false);
        SmokeAssert.True(loaded.ActiveProfileId == profileId, "The active profile was not preserved.");
        SmokeAssert.True(loaded.Profiles.Count == 1, "The profile store returned an unexpected count.");
        BackendProfile loadedProfile = loaded.Profiles[0];
        SmokeAssert.True(loadedProfile.Id == profileId, "The profile identifier was not preserved.");
        SmokeAssert.True(loadedProfile.DisableCoreUpgrade, "The core-upgrade flag was not preserved.");
        SmokeAssert.True(loadedProfile.DisableTunMode, "The TUN flag was not preserved.");
        SmokeAssert.True(
            loadedProfile.Endpoint.BaseUri.AbsoluteUri == "https://controller.example/api/v1/",
            "The controller base path was not preserved.");

        if (OperatingSystem.IsWindows())
        {
            await VerifyDpapiAsync(credentialStore, rootDirectory, profileId).ConfigureAwait(false);
        }
    }

    private static async Task VerifyDpapiAsync(
        IBackendCredentialStore credentialStore,
        string rootDirectory,
        Guid profileId)
    {
        await credentialStore.SetAsync(profileId, new BackendCredential(SmokeSecret))
            .ConfigureAwait(false);
        string credentialPath = Path.Combine(
            rootDirectory,
            "credentials",
            $"{profileId:N}.bin");
        byte[] envelope = await File.ReadAllBytesAsync(credentialPath).ConfigureAwait(false);
        SmokeAssert.True(
            envelope.AsSpan().StartsWith("ZBC1"u8),
            "The credential envelope header is invalid.");
        SmokeAssert.True(
            !Encoding.UTF8.GetString(envelope).Contains(SmokeSecret, StringComparison.Ordinal),
            "The credential envelope contains plaintext secret data.");

        BackendCredential? credential = await credentialStore.GetAsync(profileId).ConfigureAwait(false);
        SmokeAssert.True(credential?.Secret == SmokeSecret, "DPAPI did not round-trip the secret.");

        envelope[^1] ^= 0x5a;
        await File.WriteAllBytesAsync(credentialPath, envelope).ConfigureAwait(false);
        await SmokeAssert.ThrowsAsync<Win32Exception>(async () =>
        {
            _ = await credentialStore.GetAsync(profileId).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await credentialStore.DeleteAsync(profileId).ConfigureAwait(false);
        SmokeAssert.True(
            await credentialStore.GetAsync(profileId).ConfigureAwait(false) is null,
            "The credential was not deleted.");
    }

    private static async Task VerifyClashClientAsync()
    {
        CapabilityRegistry capabilities = new();
        using SmokeHttpMessageHandler handler = new(SmokeSecret);
        using HttpClient httpClient = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
        BackendProfile profile = new(
            Guid.NewGuid(),
            "Smoke controller",
            BackendEndpoint.Create("https://controller.example/api/v1/"));
        ClashRestClient client = new(
            httpClient,
            profile,
            new BackendCredential(SmokeSecret),
            capabilities);

        ClashVersion version = await client.GetVersionAsync().ConfigureAwait(false);
        SmokeAssert.True(version.CoreKind == ClashCoreKind.Mihomo, "Core detection failed.");

        ClashConfiguration configuration = await client.GetConfigurationAsync().ConfigureAwait(false);
        SmokeAssert.True(configuration.Port == 7890, "Configuration JSON mapping failed.");

        ProxyCatalog proxies = await client.GetProxiesAsync().ConfigureAwait(false);
        SmokeAssert.True(
            proxies.Proxies["GLOBAL"].Kind == ClashProxyKind.Selector,
            "Proxy JSON mapping failed.");
        await client.SelectProxyAsync("GLOBAL", "DIRECT").ConfigureAwait(false);
        SmokeAssert.True(handler.SelectionBodyValidated, "Proxy request JSON serialization failed.");

        SmartWeights weights = await client.GetSmartWeightsAsync().ConfigureAwait(false);
        SmokeAssert.True(weights.Weights["GLOBAL"][0].Weight == 1d, "Smart weight mapping failed.");
        HonkRuntimeStatistics statistics = await client.GetHonkRuntimeStatisticsAsync()
            .ConfigureAwait(false);
        SmokeAssert.True(
            statistics.Outbounds[0].TotalConnections == 3,
            "Honk statistics mapping failed.");

        await client.RestartCoreAsync().ConfigureAwait(false);
        SmokeAssert.True(
            capabilities.GetObservation(ClashCapability.CoreRestart).Support ==
                CapabilitySupport.Supported,
            "A successful optional operation did not establish capability support.");

        await SmokeAssert.ThrowsAsync<ClashProtocolException>(async () =>
        {
            _ = await client.GetProxiesAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private static async Task VerifyClashStreamAsync()
    {
        const string TrafficPayload = "{\"down\":123,\"up\":456}";
        await using SmokeWebSocketServer server = new(TrafficPayload);
        BackendProfile profile = new(
            Guid.NewGuid(),
            "Smoke stream controller",
            BackendEndpoint.Create(server.Endpoint.AbsoluteUri));
        ClashStreamClient client = new(
            profile,
            new BackendCredential(SmokeSecret),
            new CapabilityRegistry(),
            CancellationToken.None);
        List<ClashStreamStatus> statuses = [];
        client.StreamStatusChanged += (_, args) => statuses.Add(args.Status);
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(10));

        await using IAsyncEnumerator<ClashTrafficSample> enumerator = client
            .StreamTrafficAsync(guard.Token)
            .GetAsyncEnumerator(guard.Token);
        bool moved = await enumerator.MoveNextAsync().AsTask().WaitAsync(guard.Token)
            .ConfigureAwait(false);
        SmokeAssert.True(moved, "The traffic stream completed before publishing a sample.");
        SmokeAssert.True(
            enumerator.Current.Down == 123 && enumerator.Current.Up == 456,
            "WebSocket traffic JSON mapping failed.");

        string headers = await server.RequestHeaders.WaitAsync(guard.Token).ConfigureAwait(false);
        string requestLine = headers.Split("\r\n", StringSplitOptions.None)[0];
        SmokeAssert.True(
            requestLine ==
                "GET /api/v1/traffic?token=native-aot-smoke-secret HTTP/1.1",
            "The WebSocket request did not preserve its base path and token query.");
        SmokeAssert.True(
            statuses.Any(static status =>
                status.Kind == ClashStreamKind.Traffic &&
                status.State == ClashStreamState.Connected),
            "The WebSocket stream did not publish a connected state.");
    }

    private static async Task VerifyConnectionStreamsAsync()
    {
        string[] payloads =
        [
            """{"connections":[{"id":"one","download":1,"upload":2,"metadata":{"sourceGeoIP":["CN"],"destinationGeoIP":["US","CA"]}}],"downloadTotal":10,"uploadTotal":20}""",
            """{"connections":[{"id":"one","download":1,"upload":2,"metadata":{"sourceGeoIP":"CN","destinationGeoIP":null}}],"downloadTotal":10,"uploadTotal":20}""",
            """{"connections":null,"downloadTotal":10,"uploadTotal":20}""",
        ];

        for (int index = 0; index < payloads.Length; index++)
        {
            await using SmokeWebSocketServer server = new(payloads[index]);
            BackendProfile profile = new(
                Guid.NewGuid(),
                "Smoke connections controller",
                BackendEndpoint.Create(server.Endpoint.AbsoluteUri));
            ClashStreamClient client = new(
                profile,
                new BackendCredential(SmokeSecret),
                new CapabilityRegistry(),
                CancellationToken.None);
            using CancellationTokenSource guard = new(TimeSpan.FromSeconds(10));
            await using IAsyncEnumerator<ConnectionStreamSnapshot> enumerator = client
                .StreamConnectionsAsync(guard.Token)
                .GetAsyncEnumerator(guard.Token);

            bool moved = await enumerator.MoveNextAsync().AsTask().WaitAsync(guard.Token)
                .ConfigureAwait(false);
            SmokeAssert.True(moved, "The connections stream completed before publishing a snapshot.");
            ConnectionStreamSnapshot snapshot = enumerator.Current;
            SmokeAssert.True(
                snapshot.DownloadTotal == 10 && snapshot.UploadTotal == 20,
                "The connections snapshot counters were not preserved.");
            if (index == 2)
            {
                SmokeAssert.True(snapshot.Connections.Count == 0, "A null connection slice must be empty.");
                continue;
            }

            SmokeAssert.True(snapshot.Connections.Count == 1, "The connections snapshot count was invalid.");
            ClashConnectionMetadata metadata = snapshot.Connections[0].Metadata;
            SmokeAssert.True(
                metadata.SourceGeoIp.Count == 1 && metadata.SourceGeoIp[0] == "CN",
                "GeoIP array and legacy string mapping failed.");
            SmokeAssert.True(
                index == 0
                    ? metadata.DestinationGeoIp.Count == 2 &&
                        metadata.DestinationGeoIp[0] == "US" && metadata.DestinationGeoIp[1] == "CA"
                    : metadata.DestinationGeoIp.Count == 0,
                "GeoIP array and null mapping failed.");
        }
    }
}

internal static class SmokeAssert
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static async Task ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException("The expected exception was not thrown.");
    }
}

internal sealed class SmokeHttpMessageHandler(string secret) : HttpMessageHandler
{
    private int _proxyRequestCount;

    public bool SelectionBodyValidated { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SmokeAssert.True(
            request.Headers.Authorization?.Scheme == "Bearer" &&
                request.Headers.Authorization.Parameter == secret,
            "The Clash request did not contain the expected bearer credential.");
        string path = request.RequestUri?.AbsolutePath
            ?? throw new InvalidOperationException("The Clash request URI is missing.");

        if (request.Method == HttpMethod.Put && path == "/api/v1/proxies/GLOBAL")
        {
            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(body);
            SelectionBodyValidated = document.RootElement.GetProperty("name").GetString() == "DIRECT";
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        if (request.Method == HttpMethod.Post && path == "/api/v1/restart")
        {
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        return path switch
        {
            "/api/v1/version" => Json("{\"version\":\"Mihomo Meta native-aot-smoke\"}"),
            "/api/v1/configs" => Json("{\"port\":7890,\"socks-port\":7891,\"redir-port\":0,\"tproxy-port\":0,\"mixed-port\":7892,\"allow-lan\":false,\"mode\":\"rule\"}"),
            "/api/v1/proxies" => Interlocked.Increment(ref _proxyRequestCount) == 1
                ? Json("{\"proxies\":{\"GLOBAL\":{\"name\":\"GLOBAL\",\"type\":\"Selector\",\"all\":[\"DIRECT\"],\"now\":\"DIRECT\"},\"DIRECT\":{\"name\":\"DIRECT\",\"type\":\"Direct\",\"history\":[]}}}")
                : Json("{\"proxies\":{\"Broken\":{\"type\":\" \"}}}"),
            "/api/v1/group/weights" => Json("{\"message\":\"ready\",\"weights\":{\"GLOBAL\":[{\"Name\":\"DIRECT\",\"Rank\":\"1\",\"Weight\":1}]}}"),
            "/api/v1/stats" => Json("{\"outbounds\":[{\"name\":\"DIRECT\",\"totalConns\":3,\"activeConns\":1,\"upload\":10,\"download\":20,\"errors\":0}]}"),
            _ => throw new InvalidOperationException($"Unexpected Clash request: {request.Method} {path}"),
        };
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json"),
    };
}

internal sealed class SmokeWebSocketServer : IAsyncDisposable
{
    private const string WebSocketMagic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _lifetimeSource = new();
    private readonly TaskCompletionSource<string> _requestHeaders = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _serverTask;

    public SmokeWebSocketServer(string payload)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Endpoint = new Uri($"http://127.0.0.1:{port}/api/v1/");
        _serverTask = ServeAsync(payload, _lifetimeSource.Token);
    }

    public Uri Endpoint { get; }

    public Task<string> RequestHeaders => _requestHeaders.Task;

    private async Task ServeAsync(string payload, CancellationToken cancellationToken)
    {
        using TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken)
            .ConfigureAwait(false);
        await using NetworkStream stream = client.GetStream();
        string headers = await ReadHeadersAsync(stream, cancellationToken).ConfigureAwait(false);
        _requestHeaders.TrySetResult(headers);
        string key = GetHeader(headers, "Sec-WebSocket-Key");
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
        await WriteTextFrameAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
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
        if (payload.Length > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        int headerLength = payload.Length < 126 ? 2 : 4;
        byte[] frame = new byte[payload.Length + headerLength];
        frame[0] = 0x81;
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

        payload.CopyTo(frame, headerLength);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

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
        catch (SocketException) when (_lifetimeSource.IsCancellationRequested)
        {
        }
        finally
        {
            _lifetimeSource.Dispose();
        }
    }
}
