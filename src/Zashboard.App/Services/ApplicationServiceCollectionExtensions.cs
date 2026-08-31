using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zashboard.App.ViewModels;

namespace Zashboard.App.Services;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddZashboardApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IUiDispatcher>(
            static _ => SynchronizationContextUiDispatcher.CaptureCurrent());
        services.TryAddSingleton<AppSettingsState>();
        services.TryAddSingleton<AppSessionCoordinator>();
        services.TryAddSingleton<PageViewModelBinder>();

        services.TryAddSingleton<BackendSetupViewModel>();
        services.TryAddSingleton<OverviewViewModel>();
        services.TryAddSingleton<ProxiesViewModel>();
        services.TryAddSingleton<ConnectionsViewModel>();
        services.TryAddSingleton<RulesViewModel>();
        services.TryAddSingleton<LogsViewModel>();
        services.TryAddSingleton<SettingsViewModel>();

        return services;
    }
}
