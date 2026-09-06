using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Zashboard.App.Controls;
using Zashboard.App.ViewModels;

namespace Zashboard.App.Views;

public sealed partial class ProxiesPage : Page
{
    public ProxiesPage()
    {
        InitializeComponent();
        ProxyGroupCards.CollectionChanged += (_, _) => UpdateEmptyStates();
        Providers.CollectionChanged += (_, _) => UpdateEmptyStates();
        UpdateEmptyStates();
    }

    public event EventHandler? RefreshRequested;

    public event EventHandler? TestAllRequested;

    public event EventHandler? UpdateProvidersRequested;

    public event EventHandler? CheckProvidersRequested;

    public event EventHandler<ProxyProviderRequestedEventArgs>? UpdateProviderRequested;

    public event EventHandler<ProxyProviderRequestedEventArgs>? CheckProviderRequested;

    public event EventHandler<ProxyGroupRequestedEventArgs>? TestGroupRequested;

    public event EventHandler<ProxyTestRequestedEventArgs>? TestProxyRequested;

    public event EventHandler<ProxySelectionRequestedEventArgs>? ProxySelectionRequested;

    public event EventHandler<ProxyGroupRequestedEventArgs>? ClearFixedProxyRequested;

    public event EventHandler? RefreshSmartWeightsRequested;

    public event EventHandler? ResetSmartWeightsRequested;

    public ObservableCollection<ProxyGroupCardDisplayItem> ProxyGroupCards { get; } =
        new BulkObservableCollection<ProxyGroupCardDisplayItem>();

    public ObservableCollection<ProxyProviderDisplayItem> Providers { get; } =
        new BulkObservableCollection<ProxyProviderDisplayItem>();

    public object? ViewModel
    {
        get => DataContext;
        set => DataContext = value;
    }

    public void ShowMessage(string title, string message, InfoBarSeverity severity)
    {
        ProxyInfoBar.Title = title;
        ProxyInfoBar.Message = message;
        ProxyInfoBar.Severity = severity;
        AutomationProperties.SetItemStatus(ProxyInfoBar, severity.ToString());
        AutomationProperties.SetName(ProxyInfoBar, $"{title}. {message}");
        ProxyInfoBar.IsOpen = true;
    }

    public void ClearErrorMessage()
    {
        if (ProxyInfoBar.Severity == InfoBarSeverity.Error &&
            string.Equals(ProxyInfoBar.Title, "Operation failed", StringComparison.Ordinal))
        {
            ProxyInfoBar.IsOpen = false;
        }
    }

    public void ApplyProviderCapabilities(bool canUpdate, bool canCheck, bool canUseProxyActions)
    {
        RefreshProxiesButton.IsEnabled = canUseProxyActions;
        TestAllProxiesButton.IsEnabled = canUseProxyActions;
        UpdateProvidersButton.IsEnabled = canUpdate;
        CheckProvidersButton.IsEnabled = canCheck;
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs args)
    {
        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnTestAllClicked(object sender, RoutedEventArgs args)
    {
        TestAllRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnUpdateProvidersClicked(object sender, RoutedEventArgs args)
    {
        UpdateProvidersRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCheckProvidersClicked(object sender, RoutedEventArgs args)
    {
        CheckProvidersRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnUpdateProviderClicked(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: string providerName })
        {
            UpdateProviderRequested?.Invoke(this, new ProxyProviderRequestedEventArgs(providerName));
        }
    }

    private void OnCheckProviderClicked(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: string providerName })
        {
            CheckProviderRequested?.Invoke(this, new ProxyProviderRequestedEventArgs(providerName));
        }
    }

    private void OnTestGroupClicked(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: string groupName })
        {
            TestGroupRequested?.Invoke(this, new ProxyGroupRequestedEventArgs(groupName));
        }
    }

    private void OnProxyRightTapped(object sender, RightTappedRoutedEventArgs args)
    {
        if (ViewModel is ViewModelBase { CanStartUserOperation: true } &&
            sender is FrameworkElement
            {
                DataContext: ProxyNodeDisplayItem { CanTest: true } proxy,
            } &&
            !string.IsNullOrWhiteSpace(proxy.GroupName))
        {
            args.Handled = true;
            TestProxyRequested?.Invoke(
                this,
                new ProxyTestRequestedEventArgs(
                    proxy.GroupName,
                    proxy.Name,
                    proxy.Provider));
        }
    }

    private void OnSelectProxyClicked(object sender, RoutedEventArgs args)
    {
        if (sender is ToggleButton
            {
                DataContext: ProxyNodeDisplayItem { CanSelect: true } proxy,
            } toggle &&
            !string.IsNullOrWhiteSpace(proxy.GroupName))
        {
            toggle.IsChecked = proxy.IsCurrent;
            ProxySelectionRequested?.Invoke(
                this,
                new ProxySelectionRequestedEventArgs(proxy.GroupName, proxy.Name));
        }
    }

    private void OnClearFixedProxyClicked(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: string groupName })
        {
            ClearFixedProxyRequested?.Invoke(
                this,
                new ProxyGroupRequestedEventArgs(groupName));
        }
    }

    private void OnRefreshSmartWeightsClicked(object sender, RoutedEventArgs args)
    {
        RefreshSmartWeightsRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void OnResetSmartWeightsClicked(object sender, RoutedEventArgs args)
    {
        if (await ConfirmAsync(
            "Reset learned Smart weights?",
            "The controller will discard the learned ranking data for Smart proxy groups.",
            "Reset"))
        {
            ResetSmartWeightsRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UpdateEmptyStates()
    {
        GroupsEmptyState.Visibility = ProxyGroupCards.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        ProvidersEmptyState.Visibility = Providers.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async Task<bool> ConfirmAsync(string title, string message, string primaryText)
    {
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            PrimaryButtonText = primaryText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}

public sealed class ProxyProviderRequestedEventArgs(string providerName) : EventArgs
{
    public string ProviderName { get; } = providerName;
}

public sealed class ProxyGroupRequestedEventArgs(string groupName) : EventArgs
{
    public string GroupName { get; } = groupName;
}

public sealed class ProxySelectionRequestedEventArgs(string groupName, string proxyName) : EventArgs
{
    public string GroupName { get; } = groupName;

    public string ProxyName { get; } = proxyName;
}

public sealed class ProxyTestRequestedEventArgs(
    string groupName,
    string proxyName,
    string? providerName) : EventArgs
{
    public string GroupName { get; } = groupName;

    public string ProxyName { get; } = proxyName;

    public string? ProviderName { get; } = providerName;
}
