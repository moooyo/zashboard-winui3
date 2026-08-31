using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Zashboard.App.Controls;

namespace Zashboard.App.Views;

public sealed partial class ShellPage : Page
{
    private bool _isOperationInProgress;

    public ShellPage()
    {
        InitializeComponent();
    }

    public object? ViewModel
    {
        get => DataContext;
        set => DataContext = value;
    }

    public Frame NavigationFrame => ContentFrame;

    public event EventHandler<BackendSelectedEventArgs>? BackendActivationRequested;

    public void SetBackendItemsSource(IEnumerable<BackendDisplayItem>? backends)
    {
        QuickBackendsList.ItemsSource = backends;
    }

    public void Navigate(Type pageType)
    {
        ArgumentNullException.ThrowIfNull(pageType);

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }

    public void ShowSessionMessage(string title, string message, InfoBarSeverity severity)
    {
        SessionInfoBar.Title = title;
        SessionInfoBar.Message = message;
        SessionInfoBar.Severity = severity;
        AutomationProperties.SetItemStatus(SessionInfoBar, severity.ToString());
        AutomationProperties.SetName(SessionInfoBar, $"{title}. {message}");
        SessionInfoBar.IsOpen = true;
    }

    public void ClearSessionMessage()
    {
        SessionInfoBar.IsOpen = false;
    }

    public void ShowOperationMessage(string title, string message, InfoBarSeverity severity)
    {
        OperationInfoBar.Title = title;
        OperationInfoBar.Message = message;
        OperationInfoBar.Severity = severity;
        AutomationProperties.SetItemStatus(OperationInfoBar, severity.ToString());
        AutomationProperties.SetName(OperationInfoBar, $"{title}. {message}");
        OperationInfoBar.IsOpen = true;
    }

    public void ClearOperationMessage()
    {
        OperationInfoBar.IsOpen = false;
    }

    public void SetOperationInProgress(bool isRunning)
    {
        if (_isOperationInProgress == isRunning)
        {
            ContentFrame.IsEnabled = !isRunning;
            OperationProgressBar.Visibility = isRunning
                ? Visibility.Visible
                : Visibility.Collapsed;
            return;
        }

        _isOperationInProgress = isRunning;
        if (isRunning)
        {
            BackendsFlyout.Hide();
        }

        ContentFrame.IsEnabled = !isRunning;
        SetNavigationEnabled(!isRunning);
        OperationProgressBar.Visibility = isRunning
            ? Visibility.Visible
            : Visibility.Collapsed;
        string status = isRunning
            ? "Controller operation started. Page controls are temporarily unavailable."
            : "Controller operation completed. Page controls are available.";
        AutomationProperties.SetName(OperationProgressBar, status);
        AutomationProperties.SetItemStatus(
            OperationProgressBar,
            isRunning ? "In progress" : "Completed");
        AutomationPeer? peer = FrameworkElementAutomationPeer.FromElement(OperationProgressBar) ??
            FrameworkElementAutomationPeer.CreatePeerForElement(OperationProgressBar);
        peer?.RaiseNotificationEvent(
            AutomationNotificationKind.Other,
            AutomationNotificationProcessing.ImportantMostRecent,
            status,
            "ControllerOperationStatus");
    }

    private void OnShellLoaded(object sender, RoutedEventArgs args)
    {
        RootNavigation.Loaded -= OnShellLoaded;

        if (RootNavigation.SettingsItem is NavigationViewItem settingsItem)
        {
            settingsItem.AccessKey = "7";
            settingsItem.UseSystemFocusVisuals = true;
            AutomationProperties.SetHelpText(settingsItem, "Open application and controller settings.");
        }

        if (ContentFrame.Content is null)
        {
            RootNavigation.SelectedItem = OverviewNavigationItem;
            Navigate(typeof(OverviewPage));
        }
    }

    private void OnNavigationItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (_isOperationInProgress)
        {
            return;
        }

        if (args.IsSettingsInvoked)
        {
            Navigate(typeof(SettingsPage));
            return;
        }

        if (args.InvokedItemContainer is not NavigationViewItem item ||
            item.Tag is not string tag)
        {
            return;
        }

        if (string.Equals(tag, "backends", StringComparison.Ordinal))
        {
            SynchronizeNavigationSelection(ContentFrame.CurrentSourcePageType);
            BackendsFlyout.ShowAt(item);
            return;
        }

        if (GetPageType(tag) is { } pageType)
        {
            Navigate(pageType);
        }
    }

    private void OnFrameNavigated(object sender, NavigationEventArgs args)
    {
        ContentFrame.BackStack.Clear();
        RootNavigation.IsBackEnabled = false;
        SynchronizeNavigationSelection(args.SourcePageType);

        if (args.Content is OverviewPage overviewPage)
        {
            overviewPage.OpenConnectionsRequested -= OnOpenConnectionsRequested;
            overviewPage.OpenConnectionsRequested += OnOpenConnectionsRequested;
        }

        if (args.Content is SettingsPage settingsPage)
        {
            settingsPage.ManageBackendsRequested -= OnManageBackendsRequested;
            settingsPage.ManageBackendsRequested += OnManageBackendsRequested;
        }
    }

    private void OnQuickBackendClicked(object sender, ItemClickEventArgs args)
    {
        BackendsFlyout.Hide();
        if (args.ClickedItem is BackendDisplayItem { IsActive: false } backend)
        {
            BackendActivationRequested?.Invoke(this, new BackendSelectedEventArgs(backend.Id));
        }
    }

    private void OnManageBackendsClicked(object sender, RoutedEventArgs args)
    {
        BackendsFlyout.Hide();
        Navigate(typeof(BackendSetupPage));
    }

    private void OnManageBackendsRequested(object? sender, EventArgs args)
    {
        Navigate(typeof(BackendSetupPage));
    }

    private void OnOpenConnectionsRequested(object? sender, EventArgs args)
    {
        Navigate(typeof(ConnectionsPage));
    }

    private void SynchronizeNavigationSelection(Type pageType)
    {
        if (pageType == typeof(SettingsPage))
        {
            RootNavigation.SelectedItem = RootNavigation.SettingsItem;
            return;
        }

        string? expectedTag = pageType == typeof(OverviewPage) ? "overview"
            : pageType == typeof(ProxiesPage) ? "proxies"
            : pageType == typeof(ConnectionsPage) ? "connections"
            : pageType == typeof(RulesPage) ? "rules"
            : pageType == typeof(LogsPage) ? "logs"
            : pageType == typeof(BackendSetupPage) ? "backends"
            : null;

        if (expectedTag is null)
        {
            return;
        }

        NavigationViewItem? item = RootNavigation.MenuItems
            .Concat(RootNavigation.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .FirstOrDefault(candidate => string.Equals(candidate.Tag as string, expectedTag, StringComparison.Ordinal));

        if (item is not null)
        {
            RootNavigation.SelectedItem = item;
        }
    }

    private static Type? GetPageType(string tag) => tag switch
    {
        "overview" => typeof(OverviewPage),
        "proxies" => typeof(ProxiesPage),
        "connections" => typeof(ConnectionsPage),
        "rules" => typeof(RulesPage),
        "logs" => typeof(LogsPage),
        _ => null,
    };

    private void SetNavigationEnabled(bool isEnabled)
    {
        foreach (NavigationViewItem item in RootNavigation.MenuItems
            .Concat(RootNavigation.FooterMenuItems)
            .OfType<NavigationViewItem>())
        {
            item.IsEnabled = isEnabled;
        }

        if (RootNavigation.SettingsItem is NavigationViewItem settingsItem)
        {
            settingsItem.IsEnabled = isEnabled;
        }

        RootNavigation.IsBackEnabled = false;
    }
}
