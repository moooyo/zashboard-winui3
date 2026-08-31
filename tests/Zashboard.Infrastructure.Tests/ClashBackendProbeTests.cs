using System.Net;
using Zashboard.Core.Backends;
using Zashboard.Infrastructure.Clash;

namespace Zashboard.Infrastructure.Tests;

[TestClass]
public sealed class ClashBackendProbeTests
{
    private static readonly BackendProfile Profile = new(
        Guid.Parse("47232521-a675-4cc2-a816-f9795ffd3a6a"),
        "Probe backend",
        BackendEndpoint.Create("https://controller.example/api/v1/"));

    [TestMethod]
    public async Task SuccessfulProbeUsesVersionEndpointAndBearerCredential()
    {
        using RecordingHttpMessageHandler handler = new(_ =>
            new HttpResponseMessage(HttpStatusCode.OK));
        ClashBackendProbe probe = CreateProbe(handler);

        BackendProbeResult result = await probe.ProbeAsync(
            Profile,
            new BackendCredential("probe secret"),
            TimeSpan.FromSeconds(2));

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(BackendProbeFailureKind.None, result.FailureKind);
        Assert.IsTrue(result.Latency >= TimeSpan.Zero);
        Assert.AreEqual("/api/v1/version", handler.LastRequest.Uri.AbsolutePath);
        Assert.AreEqual("Bearer", handler.LastRequest.Authorization?.Scheme);
        Assert.AreEqual("probe secret", handler.LastRequest.Authorization?.Parameter);
    }

    [TestMethod]
    [DataRow(401)]
    [DataRow(403)]
    public async Task RejectedCredentialIsReportedAsUnauthorized(int statusCode)
    {
        using RecordingHttpMessageHandler handler = new(_ =>
            new HttpResponseMessage((HttpStatusCode)statusCode));
        ClashBackendProbe probe = CreateProbe(handler);

        BackendProbeResult result = await probe.ProbeAsync(
            Profile,
            new BackendCredential("bad secret"),
            TimeSpan.FromSeconds(2));

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(BackendProbeFailureKind.Unauthorized, result.FailureKind);
        Assert.AreEqual($"HTTP {statusCode}", result.Message);
    }

    [TestMethod]
    public async Task ServerFailureIsReportedAsHttp()
    {
        using RecordingHttpMessageHandler handler = new(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        ClashBackendProbe probe = CreateProbe(handler);

        BackendProbeResult result = await probe.ProbeAsync(
            Profile,
            new BackendCredential(string.Empty),
            TimeSpan.FromSeconds(2));

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(BackendProbeFailureKind.Http, result.FailureKind);
        Assert.AreEqual("HTTP 503", result.Message);
    }

    [TestMethod]
    public async Task TransportFailureWithoutStatusIsReportedAsNetwork()
    {
        using ThrowingHttpMessageHandler handler = new();
        ClashBackendProbe probe = CreateProbe(handler);

        BackendProbeResult result = await probe.ProbeAsync(
            Profile,
            new BackendCredential(string.Empty),
            TimeSpan.FromSeconds(2));

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(BackendProbeFailureKind.Network, result.FailureKind);
        Assert.AreEqual("The network is unavailable.", result.Message);
    }

    [TestMethod]
    public async Task ProbeTimeoutReturnsTimeoutFailure()
    {
        using NeverCompletingHttpMessageHandler handler = new();
        ClashBackendProbe probe = CreateProbe(handler);

        BackendProbeResult result = await probe.ProbeAsync(
            Profile,
            new BackendCredential(string.Empty),
            TimeSpan.FromMilliseconds(50));

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(BackendProbeFailureKind.Timeout, result.FailureKind);
        Assert.AreEqual("The backend probe timed out.", result.Message);
    }

    [TestMethod]
    public async Task CallerCancellationIsPropagated()
    {
        using NeverCompletingHttpMessageHandler handler = new();
        ClashBackendProbe probe = CreateProbe(handler);
        using CancellationTokenSource cancellationSource = new();
        Task<BackendProbeResult> operation = probe.ProbeAsync(
            Profile,
            new BackendCredential(string.Empty),
            TimeSpan.FromSeconds(10),
            cancellationSource.Token);

        await handler.RequestStarted.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellationSource.CancelAsync();

        Exception? exception = null;
        try
        {
            _ = await operation;
        }
        catch (Exception caught)
        {
            exception = caught;
        }

        Assert.IsInstanceOfType<OperationCanceledException>(exception);
    }

    [TestMethod]
    public async Task NewlineCredentialIsRejectedBeforeSendingRequest()
    {
        using RecordingHttpMessageHandler handler = new(_ =>
            new HttpResponseMessage(HttpStatusCode.OK));
        ClashBackendProbe probe = CreateProbe(handler);

        _ = await Assert.ThrowsExactlyAsync<ArgumentException>(() => probe.ProbeAsync(
            Profile,
            new BackendCredential("invalid\r\ncredential"),
            TimeSpan.FromSeconds(2)));

        Assert.IsEmpty(handler.Requests);
    }

    private static ClashBackendProbe CreateProbe(HttpMessageHandler handler)
    {
        HttpClient client = new(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        return new ClashBackendProbe(new StubHttpClientFactory(client));
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.AreEqual("Zashboard.Clash", name);
            return client;
        }
    }

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("The network is unavailable.");
    }

    private sealed class NeverCompletingHttpMessageHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _requestStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RequestStarted => _requestStarted.Task;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _requestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("The request unexpectedly completed.");
        }
    }
}
