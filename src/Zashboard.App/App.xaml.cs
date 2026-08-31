using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Zashboard.App.Services;
using Zashboard.Infrastructure;

namespace Zashboard.App;

public partial class App : Application
{
    private const string MainInstanceKey = "Zashboard.Main";

    private readonly IHost _host;
    private MainWindow? _window;
    private AppInstance? _mainInstance;
    private int _shutdownStarted;

    public App()
    {
        InitializeComponent();

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.AddDebug();
        builder.Services.AddZashboardInfrastructure();
        builder.Services.AddZashboardApplication();
        _host = builder.Build();
    }

    public IServiceProvider Services => _host.Services;

    internal MainWindow? MainWindow => _window;

    internal bool IsShuttingDown => Volatile.Read(ref _shutdownStarted) != 0;

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _mainInstance = AppInstance.FindOrRegisterForKey(MainInstanceKey);
            if (!_mainInstance.IsCurrent)
            {
                await _mainInstance.RedirectActivationToAsync(
                    AppInstance.GetCurrent().GetActivatedEventArgs());
                await ShutdownAsync();
                return;
            }

            _mainInstance.Activated += OnInstanceActivated;
            await _host.StartAsync();

            _window = new MainWindow(Services);
            _window.Closed += OnMainWindowClosed;
            _window.Activate();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Application startup failed: {exception}");
            await ShutdownAsync();
        }
    }

    public async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
        {
            return;
        }

        if (_mainInstance is not null)
        {
            _mainInstance.Activated -= OnInstanceActivated;
        }

        try
        {
            _window?.PrepareForShutdown();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Window shutdown preparation failed: {exception}");
        }

        try
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Application stop failed: {exception}");
        }

        try
        {
            if (_host is IAsyncDisposable asyncHost)
            {
                await asyncHost.DisposeAsync();
            }
            else
            {
                _host.Dispose();
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Application disposal failed: {exception}");
        }
        finally
        {
            _window?.ReleaseTrayIcon();
            Exit();
        }
    }

    private void OnInstanceActivated(object? sender, AppActivationArguments args)
    {
        DispatcherQueue? dispatcher = _window?.DispatcherQueue;
        dispatcher?.TryEnqueue(() =>
        {
            _window?.Activate();
        });
    }

    private async void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        await ShutdownAsync();
    }
}
