using Zashboard.Core.Backends;
using Zashboard.Core.Capabilities;
using Zashboard.Core.Clash;
using Zashboard.Core.Sessions;
using Zashboard.Infrastructure.Clash;

namespace Zashboard.Infrastructure.Sessions;

public sealed class BackendSessionFactory : IBackendSessionFactory
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ClashWebSocketOptions _webSocketOptions;
    private readonly BackendSessionOptions _sessionOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ClashHttpOptions _httpOptions;

    public BackendSessionFactory(
        IHttpClientFactory httpClientFactory,
        ClashWebSocketOptions? webSocketOptions = null,
        BackendSessionOptions? sessionOptions = null,
        TimeProvider? timeProvider = null,
        ClashHttpOptions? httpOptions = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _webSocketOptions = webSocketOptions ?? new ClashWebSocketOptions();
        _webSocketOptions.Validate();
        _sessionOptions = sessionOptions ?? new BackendSessionOptions();
        _sessionOptions.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _httpOptions = httpOptions ?? new ClashHttpOptions();
        _httpOptions.Validate();
    }

    public async ValueTask<IBackendSession> CreateAsync(
        BackendProfile profile,
        BackendCredential credential,
        SessionEpoch epoch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(credential);
        cancellationToken.ThrowIfCancellationRequested();

        HttpClient httpClient = _httpClientFactory.CreateClient(
            InfrastructureServiceCollectionExtensions.ClashHttpClientName);
        CancellationTokenSource lifetimeSource = new();
        CapabilityRegistry capabilities = new();
        ClashRestClient restClient = new(
            httpClient,
            profile,
            credential,
            capabilities,
            _timeProvider,
            _httpOptions);
        ClashStreamClient streamClient = new(
            profile,
            credential,
            capabilities,
            lifetimeSource.Token,
            _webSocketOptions,
            _timeProvider);

        DateTimeOffset now = _timeProvider.GetUtcNow();
        BackendSession session = new(
            epoch,
            profile,
            httpClient,
            lifetimeSource,
            restClient,
            streamClient,
            capabilities,
            new BackendSessionSnapshot
            {
                Epoch = epoch,
                ProfileId = profile.Id,
                State = BackendConnectionState.Connecting,
                StateChangedAt = now,
                Capabilities = capabilities.GetSnapshot(),
            });

        using CancellationTokenSource initializationTimeout = new(
            _sessionOptions.InitializationTimeout,
            _timeProvider);
        using CancellationTokenSource initialization = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetimeSource.Token,
            initializationTimeout.Token);

        try
        {
            ClashVersion version = await restClient.GetVersionAsync(initialization.Token).ConfigureAwait(false);
            DateTimeOffset contactedAt = _timeProvider.GetUtcNow();
            session.UpdateSnapshot(new BackendSessionSnapshot
            {
                Epoch = epoch,
                ProfileId = profile.Id,
                State = BackendConnectionState.Online,
                CoreKind = version.CoreKind,
                Version = version.Value,
                StateChangedAt = contactedAt,
                LastSuccessfulContactAt = contactedAt,
                Capabilities = capabilities.GetSnapshot(),
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException) when (initializationTimeout.IsCancellationRequested)
        {
            session.UpdateSnapshot(FailureSnapshot(
                epoch,
                profile.Id,
                BackendConnectionState.OfflineRetrying,
                "The initial backend connection timed out.",
                capabilities));
        }
        catch (ClashAuthenticationException exception)
        {
            session.UpdateSnapshot(FailureSnapshot(
                epoch,
                profile.Id,
                BackendConnectionState.Unauthorized,
                exception.Message,
                capabilities));
        }
        catch (ClashProtocolException exception)
        {
            session.UpdateSnapshot(FailureSnapshot(
                epoch,
                profile.Id,
                BackendConnectionState.Degraded,
                exception.Message,
                capabilities));
        }
        catch (ClashApiException exception)
        {
            BackendConnectionState state = (int?)exception.StatusCode is null or 408 or 429 or >= 500 and <= 599
                ? BackendConnectionState.OfflineRetrying
                : BackendConnectionState.Degraded;
            session.UpdateSnapshot(FailureSnapshot(
                epoch,
                profile.Id,
                state,
                exception.Message,
                capabilities));
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return session;
    }

    private BackendSessionSnapshot FailureSnapshot(
        SessionEpoch epoch,
        Guid profileId,
        BackendConnectionState state,
        string detail,
        CapabilityRegistry capabilities) => new()
    {
        Epoch = epoch,
        ProfileId = profileId,
        State = state,
        StateChangedAt = _timeProvider.GetUtcNow(),
        StatusDetail = detail,
        Capabilities = capabilities.GetSnapshot(),
    };
}
