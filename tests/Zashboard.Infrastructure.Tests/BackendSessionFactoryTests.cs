using System.Net;
using System.Text;
using Zashboard.Core.Backends;
using Zashboard.Core.Capabilities;
using Zashboard.Core.Clash;
using Zashboard.Core.Sessions;
using Zashboard.Infrastructure.Clash;
using Zashboard.Infrastructure.Sessions;

namespace Zashboard.Infrastructure.Tests;

[TestClass]
public sealed class BackendSessionFactoryTests
{
    private static readonly DateTimeOffset ObservedAt = new(
        2026,
        8,
        30,
        9,
        10,
        11,
        TimeSpan.Zero);

    private static readonly BackendProfile Profile = new(
        Guid.Parse("56ad79c1-0985-4f80-aeaf-5d2860183f72"),
        "Session backend",
        BackendEndpoint.Create("https://controller.example/api/"));

    [TestMethod]
    [DataRow(401)]
    [DataRow(403)]
    public async Task RejectedVersionResponseCreatesUnauthorizedSession(int statusCode)
    {
        BackendSessionFactory factory = CreateFactory(new DelegateHttpMessageHandler(
            _ => new HttpResponseMessage((HttpStatusCode)statusCode)
            {
                Content = new StringContent(
                    "{\"message\":\"bad session-secret\"}",
                    Encoding.UTF8,
                    "application/json"),
            }));

        await using IBackendSession session = await factory.CreateAsync(
            Profile,
            new BackendCredential("session-secret"),
            new SessionEpoch(11));

        Assert.AreEqual(BackendConnectionState.Unauthorized, session.Snapshot.State);
        Assert.AreEqual(Profile.Id, session.Snapshot.ProfileId);
        Assert.AreEqual(new SessionEpoch(11), session.Snapshot.Epoch);
        Assert.AreEqual("The Clash API rejected the backend credential.", session.Snapshot.StatusDetail);
        Assert.IsNull(session.Snapshot.LastSuccessfulContactAt);
    }

    [TestMethod]
    public async Task MalformedVersionResponseCreatesDegradedSession()
    {
        BackendSessionFactory factory = CreateFactory(new DelegateHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{", Encoding.UTF8, "application/json"),
            }));

        await using IBackendSession session = await factory.CreateAsync(
            Profile,
            new BackendCredential(string.Empty),
            new SessionEpoch(12));

        Assert.AreEqual(BackendConnectionState.Degraded, session.Snapshot.State);
        Assert.AreEqual(
            "The Clash API returned malformed or incompatible JSON.",
            session.Snapshot.StatusDetail);
        Assert.IsNull(session.Snapshot.LastSuccessfulContactAt);
    }

    [TestMethod]
    public async Task VersionResponseMissingRequiredFieldCreatesDegradedSession()
    {
        BackendSessionFactory factory = CreateFactory(new DelegateHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            }));

        await using IBackendSession session = await factory.CreateAsync(
            Profile,
            new BackendCredential(string.Empty),
            new SessionEpoch(17));

        Assert.AreEqual(BackendConnectionState.Degraded, session.Snapshot.State);
        Assert.AreEqual(
            "The Clash API returned malformed or incompatible JSON.",
            session.Snapshot.StatusDetail);
        Assert.IsNull(session.Snapshot.LastSuccessfulContactAt);
    }

    [TestMethod]
    public async Task InitializationTimeoutCreatesOfflineRetryingSession()
    {
        BackendSessionFactory factory = CreateFactory(
            new NeverCompletingHttpMessageHandler(),
            initializationTimeout: TimeSpan.FromMilliseconds(50));

        await using IBackendSession session = await factory.CreateAsync(
            Profile,
            new BackendCredential(string.Empty),
            new SessionEpoch(13));

        Assert.AreEqual(BackendConnectionState.OfflineRetrying, session.Snapshot.State);
        Assert.AreEqual(
            "The initial backend connection timed out.",
            session.Snapshot.StatusDetail);
        Assert.IsNull(session.Snapshot.LastSuccessfulContactAt);
    }

    [TestMethod]
    public async Task CallerCancellationIsPropagatedDuringInitialization()
    {
        NeverCompletingHttpMessageHandler handler = new();
        BackendSessionFactory factory = CreateFactory(
            handler,
            initializationTimeout: TimeSpan.FromSeconds(10));
        using CancellationTokenSource cancellationSource = new();
        Task<IBackendSession> creation = factory.CreateAsync(
            Profile,
            new BackendCredential(string.Empty),
            new SessionEpoch(14),
            cancellationSource.Token).AsTask();
        await handler.RequestStarted.WaitAsync(TimeSpan.FromSeconds(5));

        await cancellationSource.CancelAsync();
        Exception exception = await CaptureExceptionAsync(creation);

        Assert.IsInstanceOfType<OperationCanceledException>(exception);
        await handler.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task SuccessfulVersionProbeCreatesOnlineSessionAndTracksCapabilities()
    {
        BackendSessionFactory factory = CreateFactory(
            new DelegateHttpMessageHandler(request =>
                request.RequestUri?.AbsolutePath.EndsWith("/version", StringComparison.Ordinal) == true
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            "{\"version\":\"honk 0.4.0\"}",
                            Encoding.UTF8,
                            "application/json"),
                    }
                    : new HttpResponseMessage(HttpStatusCode.NoContent)),
            timeProvider: new FixedTimeProvider(ObservedAt));

        await using IBackendSession session = await factory.CreateAsync(
            Profile,
            new BackendCredential(string.Empty),
            new SessionEpoch(15));

        BackendSessionSnapshot snapshot = session.Snapshot;
        Assert.AreEqual(BackendConnectionState.Online, snapshot.State);
        Assert.AreEqual(ClashCoreKind.Honk, snapshot.CoreKind);
        Assert.AreEqual("honk 0.4.0", snapshot.Version);
        Assert.AreEqual(Profile.Id, snapshot.ProfileId);
        Assert.AreEqual(new SessionEpoch(15), snapshot.Epoch);
        Assert.AreEqual(ObservedAt, snapshot.StateChangedAt);
        Assert.AreEqual(ObservedAt, snapshot.LastSuccessfulContactAt);

        await session.RestClient.FlushSmartWeightsAsync();

        Assert.AreEqual(
            CapabilitySupport.Supported,
            session.Snapshot.Capabilities[ClashCapability.SmartWeightReset].Support);
    }

    [TestMethod]
    public async Task DisposeCancelsLifetimeAndIsIdempotent()
    {
        BackendSessionFactory factory = CreateFactory(new DelegateHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"version\":\"Mihomo Meta v1.19.0\"}",
                    Encoding.UTF8,
                    "application/json"),
            }));
        IBackendSession session = await factory.CreateAsync(
            Profile,
            new BackendCredential(string.Empty),
            new SessionEpoch(16));
        CancellationToken lifetime = session.Lifetime;

        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.IsTrue(lifetime.IsCancellationRequested);
    }

    private static BackendSessionFactory CreateFactory(
        HttpMessageHandler handler,
        TimeSpan? initializationTimeout = null,
        TimeProvider? timeProvider = null)
    {
        HttpClient client = new(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        return new BackendSessionFactory(
            new StubHttpClientFactory(client),
            sessionOptions: new BackendSessionOptions
            {
                InitializationTimeout = initializationTimeout ?? TimeSpan.FromSeconds(5),
            },
            timeProvider: timeProvider,
            httpOptions: new ClashHttpOptions
            {
                OperationTimeout = TimeSpan.FromSeconds(30),
            });
    }

    private static async Task<Exception> CaptureExceptionAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new AssertFailedException("The operation completed without an exception.");
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.AreEqual("Zashboard.Clash", name);
            return client;
        }
    }

    private sealed class DelegateHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class NeverCompletingHttpMessageHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _requestStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationObserved = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RequestStarted => _requestStarted.Task;

        public Task CancellationObserved => _cancellationObserved.Task;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _requestStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _cancellationObserved.TrySetResult();
                throw;
            }

            throw new InvalidOperationException("The request unexpectedly completed.");
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
