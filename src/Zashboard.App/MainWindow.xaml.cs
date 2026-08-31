using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Storage;
using WinUIEx;
using Zashboard.App.Services;
using Zashboard.App.Views;

namespace Zashboard.App;

[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The WinUI Window lifecycle calls PrepareForShutdown before Application.Exit.")]
public sealed partial class MainWindow : Window
{
    private const int MinimumWidth = 720;
    private const int MinimumHeight = 520;
    private const uint TrayIconId = 1;

    private readonly AppSessionCoordinator _sessionCoordinator;
    private readonly AppSettingsState _settings;
    private readonly PageViewModelBinder _pageViewModelBinder;
    private readonly TrayIcon _trayIcon;

    private PointInt32 _restoredPosition;
    private SizeInt32 _restoredSize;
    private bool _hasRestoredBounds;
    private bool _isApplyingMinimumSize;
    private int _cleanupState;
    private int _trayCleanupState;

    public MainWindow(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        InitializeComponent();
        Title = "Zashboard";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        _sessionCoordinator = services.GetRequiredService<AppSessionCoordinator>();
        _settings = services.GetRequiredService<AppSettingsState>();
        _pageViewModelBinder = services.GetRequiredService<PageViewModelBinder>();
        _pageViewModelBinder.Attach(Shell);

        this.SetIcon("Assets\\AppIcon.ico");
        this.CenterOnScreen(1180, 760);
        RestoreWindowPlacement();
        AppWindow.Changed += OnAppWindowChanged;

        _trayIcon = new TrayIcon(TrayIconId, "Assets\\AppIcon.ico", "Zashboard");
        _trayIcon.Selected += OnTrayIconSelected;
        _trayIcon.LeftDoubleClick += OnTrayIconSelected;
        _trayIcon.ContextMenu += OnTrayIconContextMenu;
        _trayIcon.IsVisible = true;
        AppWindow.Closing += OnAppWindowClosing;
        _settings.PropertyChanged += OnSettingsPropertyChanged;
        WindowRoot.ActualThemeChanged += OnWindowRootActualThemeChanged;
        ApplyWindowSettings();

        Shell.Loaded += OnShellLoaded;
    }

    private async void OnShellLoaded(object sender, RoutedEventArgs args)
    {
        Shell.Loaded -= OnShellLoaded;
        try
        {
            await _sessionCoordinator.RunUserOperationAsync(
                _sessionCoordinator.InitializeAsync);
            if (_sessionCoordinator.Profiles.Count == 0)
            {
                Shell.Navigate(typeof(BackendSetupPage));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Shell.ShowSessionMessage(
                "Application startup",
                exception.Message,
                InfoBarSeverity.Error);
        }
    }

    internal void PrepareForShutdown()
    {
        if (Interlocked.Exchange(ref _cleanupState, 1) != 0)
        {
            return;
        }

        try
        {
            SaveWindowPlacement();
            Shell.Loaded -= OnShellLoaded;
            AppWindow.Changed -= OnAppWindowChanged;
            AppWindow.Closing -= OnAppWindowClosing;
            _settings.PropertyChanged -= OnSettingsPropertyChanged;
            WindowRoot.ActualThemeChanged -= OnWindowRootActualThemeChanged;
            _trayIcon.Selected -= OnTrayIconSelected;
            _trayIcon.LeftDoubleClick -= OnTrayIconSelected;
            _trayIcon.ContextMenu -= OnTrayIconContextMenu;
            _trayIcon.IsVisible = false;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Window cleanup failed: {exception}");
        }
    }

    internal void ReleaseTrayIcon()
    {
        if (Interlocked.Exchange(ref _trayCleanupState, 1) != 0)
        {
            return;
        }

        try
        {
            _trayIcon.Dispose();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Tray icon cleanup failed: {exception}");
        }
    }

    private void OnTrayIconSelected(TrayIcon sender, TrayIconEventArgs args)
    {
        args.Handled = true;
        ShowMainWindow();
    }

    private void OnTrayIconContextMenu(TrayIcon sender, TrayIconEventArgs args)
    {
        MenuFlyout flyout = new();

        MenuFlyoutItem openItem = new() { Text = "Open Zashboard" };
        openItem.Click += (_, _) => ShowMainWindow();
        flyout.Items.Add(openItem);

        MenuFlyoutItem reconnectItem = new() { Text = "Reconnect" };
        reconnectItem.IsEnabled = _sessionCoordinator.ActiveProfile is not null &&
            !_sessionCoordinator.IsUserOperationRunning;
        reconnectItem.Click += async (_, _) => await ReconnectFromTrayAsync();
        flyout.Items.Add(reconnectItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        MenuFlyoutItem exitItem = new() { Text = "Exit" };
        exitItem.Click += async (_, _) => await ((App)Application.Current).ShutdownAsync();
        flyout.Items.Add(exitItem);

        args.Flyout = flyout;
    }

    private async Task ReconnectFromTrayAsync()
    {
        Shell.ClearOperationMessage();
        try
        {
            await _sessionCoordinator.RunUserOperationAsync(
                _sessionCoordinator.ReconnectAsync);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Shell.ShowOperationMessage(
                "Reconnect failed",
                exception.Message,
                InfoBarSeverity.Error);
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        SaveWindowPlacement();
        if (!((App)Application.Current).IsShuttingDown &&
            string.Equals(_settings.CloseBehavior, "hideToTray", StringComparison.Ordinal))
        {
            args.Cancel = true;
            sender.Hide();
        }
    }

    private void ShowMainWindow()
    {
        AppWindow.Show();
        Activate();
        _ = this.SetForegroundWindow();
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_isApplyingMinimumSize)
        {
            return;
        }

        if (args.DidSizeChange)
        {
            double scale = Math.Max(1d, this.GetDpiForWindow() / 96d);
            int minimumWidth = checked((int)Math.Ceiling(MinimumWidth * scale));
            int minimumHeight = checked((int)Math.Ceiling(MinimumHeight * scale));
            SizeInt32 size = sender.Size;
            int width = Math.Max(size.Width, minimumWidth);
            int height = Math.Max(size.Height, minimumHeight);
            if (width != size.Width || height != size.Height)
            {
                _isApplyingMinimumSize = true;
                try
                {
                    sender.Resize(new SizeInt32(width, height));
                }
                finally
                {
                    _isApplyingMinimumSize = false;
                }
            }
        }

        if ((args.DidPositionChange || args.DidSizeChange || args.DidPresenterChange) &&
            sender.Presenter is OverlappedPresenter
            {
                State: OverlappedPresenterState.Restored,
            })
        {
            _restoredPosition = sender.Position;
            _restoredSize = sender.Size;
            _hasRestoredBounds = true;
        }
    }

    private void RestoreWindowPlacement()
    {
        ApplicationDataContainer? storage = TryGetLocalSettings();
        if (storage is null ||
            !TryReadInt32(storage, "window.x", out int x) ||
            !TryReadInt32(storage, "window.y", out int y) ||
            !TryReadInt32(storage, "window.width", out int width) ||
            !TryReadInt32(storage, "window.height", out int height) ||
            width <= 0 ||
            height <= 0)
        {
            CaptureRestoredBounds();
            return;
        }

        try
        {
            DisplayArea display = DisplayArea.GetFromPoint(
                new PointInt32(x, y),
                DisplayAreaFallback.Nearest);
            RectInt32 workArea = display.WorkArea;
            double scale = Math.Max(1d, this.GetDpiForWindow() / 96d);
            int minimumWidth = Math.Min(
                workArea.Width,
                checked((int)Math.Ceiling(MinimumWidth * scale)));
            int minimumHeight = Math.Min(
                workArea.Height,
                checked((int)Math.Ceiling(MinimumHeight * scale)));
            width = Math.Clamp(width, minimumWidth, workArea.Width);
            height = Math.Clamp(height, minimumHeight, workArea.Height);
            x = Math.Clamp(x, workArea.X, workArea.X + workArea.Width - width);
            y = Math.Clamp(y, workArea.Y, workArea.Y + workArea.Height - height);
            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
            CaptureRestoredBounds();

            if (storage.Values.TryGetValue("window.maximized", out object? value) &&
                value is bool maximized &&
                maximized &&
                AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Maximize();
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Window placement could not be restored: {exception.Message}");
            CaptureRestoredBounds();
        }
    }

    private void CaptureRestoredBounds()
    {
        _restoredPosition = AppWindow.Position;
        _restoredSize = AppWindow.Size;
        _hasRestoredBounds = true;
    }

    private void SaveWindowPlacement()
    {
        ApplicationDataContainer? storage = TryGetLocalSettings();
        if (storage is null)
        {
            return;
        }

        try
        {
            if (!_hasRestoredBounds)
            {
                CaptureRestoredBounds();
            }

            storage.Values["window.x"] = _restoredPosition.X;
            storage.Values["window.y"] = _restoredPosition.Y;
            storage.Values["window.width"] = _restoredSize.Width;
            storage.Values["window.height"] = _restoredSize.Height;
            storage.Values["window.maximized"] = AppWindow.Presenter is OverlappedPresenter
            {
                State: OverlappedPresenterState.Maximized,
            };
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Window placement could not be saved: {exception.Message}");
        }
    }

    private static bool TryReadInt32(
        ApplicationDataContainer storage,
        string key,
        out int value)
    {
        if (storage.Values.TryGetValue(key, out object? stored) && stored is int integer)
        {
            value = integer;
            return true;
        }

        value = 0;
        return false;
    }

    private static ApplicationDataContainer? TryGetLocalSettings()
    {
        try
        {
            return ApplicationData.Current.LocalSettings;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(AppSettingsState.Theme) or
            nameof(AppSettingsState.MicaBackdrop))
        {
            ApplyWindowSettings();
        }
    }

    private void OnWindowRootActualThemeChanged(FrameworkElement sender, object args)
    {
        UpdateTitleBarButtonColors();
    }

    private void ApplyWindowSettings()
    {
        WindowRoot.RequestedTheme = _settings.Theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        bool useMica = _settings.MicaBackdrop && MicaController.IsSupported();
        SystemBackdrop = useMica ? new MicaBackdrop() : null;
        OpaqueFallbackBackground.Visibility = useMica
            ? Visibility.Collapsed
            : Visibility.Visible;
        UpdateTitleBarButtonColors();
    }

    private void UpdateTitleBarButtonColors()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        Windows.UI.Color foreground = WindowRoot.ActualTheme == ElementTheme.Dark
            ? Colors.White
            : Colors.Black;
        AppWindow.TitleBar.ButtonForegroundColor = foreground;
        AppWindow.TitleBar.ButtonHoverForegroundColor = foreground;
        AppWindow.TitleBar.ButtonPressedForegroundColor = foreground;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = foreground;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
    }
}
