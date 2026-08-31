using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zashboard.Core.Abstractions;
using Zashboard.Core.Sessions;
using Zashboard.Infrastructure.Clash;
using Zashboard.Infrastructure.Persistence;
using Zashboard.Infrastructure.Sessions;

namespace Zashboard.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    internal const string ClashHttpClientName = "Zashboard.Clash";

    public static IServiceCollection AddZashboardInfrastructure(this IServiceCollection services) =>
        AddZashboardInfrastructure(services, InfrastructureStorageOptions.CreateDefault());

    public static IServiceCollection AddZashboardInfrastructure(
        this IServiceCollection services,
        InfrastructureStorageOptions storageOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(storageOptions);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(storageOptions);
        services.TryAddSingleton<ClashHttpOptions>();
        services.TryAddSingleton<ClashWebSocketOptions>();
        services.TryAddSingleton<BackendSessionOptions>();
        services.AddHttpClient(ClashHttpClientName, static client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Zashboard-WinUI/1.0");
        });
        services.TryAddSingleton<IBackendProbe, ClashBackendProbe>();
        services.TryAddSingleton<IBackendSessionFactory, BackendSessionFactory>();
        services.TryAddSingleton<IBackendProfileStore, JsonBackendProfileStore>();
        services.TryAddSingleton<IBackendCredentialStore, WindowsDpapiCredentialStore>();

        return services;
    }
}
