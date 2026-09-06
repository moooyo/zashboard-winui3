using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Zashboard.App.Controls;
using Zashboard.App.ViewModels;

namespace Zashboard.App.Views;

public sealed partial class RulesPage : Page
{
    private bool _isApplyingViewState;
    private bool _isShowingRuleDetails;
    private bool _isReplacingRules;
    private (int Index, string? Identifier)? _selectedRuleKey;

    public RulesPage()
    {
        InitializeComponent();
        Rules.CollectionChanged += (_, _) => UpdateEmptyState();
        Rules.Resetting += OnRulesResetting;
        Rules.ResetCompleted += OnRulesResetCompleted;
        RuleProviders.CollectionChanged += (_, _) => UpdateEmptyState();
        UpdateCommandContext();
        UpdateEmptyState();
    }

    public event EventHandler? RefreshRequested;

    public event EventHandler<RuleQueryChangedEventArgs>? QueryChanged;

    public event EventHandler<RuleToggleRequestedEventArgs>? RuleToggleRequested;

    public event EventHandler? UpdateProvidersRequested;

    public event EventHandler<RuleProviderRequestedEventArgs>? UpdateProviderRequested;

    public BulkObservableCollection<RuleDisplayItem> Rules { get; } = [];

    public ObservableCollection<RuleProviderDisplayItem> RuleProviders { get; } =
        new BulkObservableCollection<RuleProviderDisplayItem>();

    public object? ViewModel
    {
        get => DataContext;
        set => DataContext = value;
    }

    public void ShowMessage(string title, string message, InfoBarSeverity severity)
    {
        RulesInfoBar.Title = title;
        RulesInfoBar.Message = message;
        RulesInfoBar.Severity = severity;
        AutomationProperties.SetItemStatus(RulesInfoBar, severity.ToString());
        AutomationProperties.SetName(RulesInfoBar, $"{title}. {message}");
        RulesInfoBar.IsOpen = true;
    }

    public void ClearErrorMessage()
    {
        if (RulesInfoBar.Severity == InfoBarSeverity.Error &&
            string.Equals(RulesInfoBar.Title, "Operation failed", StringComparison.Ordinal))
        {
            RulesInfoBar.IsOpen = false;
        }
    }

    public void ApplyProviderCapabilities(bool canUpdate, bool canRefresh)
    {
        UpdateRuleProvidersButton.IsEnabled = canUpdate;
        RefreshRulesButton.IsEnabled = canRefresh;
    }

    public void ApplyViewState(string query, string filter)
    {
        _isApplyingViewState = true;
        try
        {
            RuleSearchBox.Text = query;
            RuleFilterBox.SelectedItem = RuleFilterBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag as string,
                    filter,
                    StringComparison.Ordinal)) ?? RuleFilterBox.Items[0];
        }
        finally
        {
            _isApplyingViewState = false;
        }

        UpdateEmptyState();
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs args)
    {
        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnUpdateProvidersClicked(object sender, RoutedEventArgs args)
    {
        UpdateProvidersRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnUpdateProviderClicked(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: string providerName })
        {
            UpdateProviderRequested?.Invoke(this, new RuleProviderRequestedEventArgs(providerName));
        }
    }

    private void OnRulesPivotSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        UpdateCommandContext();
    }

    private void OnFocusSearchAccelerator(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (RulesPivot.SelectedIndex != 0)
        {
            RulesPivot.SelectedIndex = 0;
        }

        args.Handled = RuleSearchBox.Focus(FocusState.Keyboard);
    }

    private void OnRuleToggleClicked(object sender, RoutedEventArgs args)
    {
        if (sender is ToggleButton toggle && toggle.DataContext is RuleDisplayItem rule)
        {
            bool requestedEnabled = toggle.IsChecked == true;
            toggle.IsChecked = rule.Enabled;
            RaiseRuleToggle(rule, disabled: !requestedEnabled);
        }
    }

    private void OnRuleSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (!_isApplyingViewState &&
            args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            RaiseQueryChanged();
        }
    }

    private void OnRuleFilterChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_isApplyingViewState)
        {
            RaiseQueryChanged();
        }
    }

    private void OnRuleSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_isReplacingRules)
        {
            return;
        }

        _selectedRuleKey = RulesList.SelectedItem is RuleDisplayItem rule
            ? GetRuleKey(rule)
            : null;
    }

    private async void OnRuleItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not RuleDisplayItem rule || _isShowingRuleDetails)
        {
            return;
        }

        RulesList.SelectedItem = rule;
        _selectedRuleKey = GetRuleKey(rule);
        _isShowingRuleDetails = true;
        try
        {
            await ShowRuleDetailsDialogAsync(rule);
        }
        finally
        {
            _isShowingRuleDetails = false;
        }
    }

    private async Task ShowRuleDetailsDialogAsync(RuleDisplayItem rule)
    {
        StackPanel details = new()
        {
            MinWidth = 360,
            MaxWidth = 560,
            Spacing = 14,
        };
        details.Children.Add(CreateDetailField("Type", rule.Type));
        details.Children.Add(CreateDetailField("Order", rule.Index.ToString(
            System.Globalization.CultureInfo.CurrentCulture)));
        details.Children.Add(CreateDetailField("State", rule.State));
        details.Children.Add(CreateDetailField("Payload", rule.Payload, monospace: true));
        details.Children.Add(CreateDetailField("Target", rule.Proxy));
        details.Children.Add(CreateDetailField("Provider", rule.Provider));
        details.Children.Add(CreateDetailField("Identifier", rule.Identifier, monospace: true));
        details.Children.Add(CreateDetailField(
            "Statistics",
            $"{rule.HitCount} hits, {rule.MissCount} misses"));

        ScrollViewer content = new()
        {
            Content = details,
            MaxHeight = 520,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = $"Rule {rule.Index}",
            Content = content,
            PrimaryButtonText = rule.CanToggle
                ? rule.Enabled ? "Disable rule" : "Enable rule"
                : string.Empty,
            CloseButtonText = "Close",
            IsPrimaryButtonEnabled = ViewModel is ViewModelBase { CanStartUserOperation: true },
            DefaultButton = ContentDialogButton.Close,
        };
        AutomationProperties.SetName(dialog, rule.AccessibleDescription);
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            RaiseRuleToggle(rule, disabled: rule.Enabled);
        }
    }

    private static StackPanel CreateDetailField(
        string label,
        string value,
        bool monospace = false)
    {
        TextBlock valueText = new()
        {
            IsTextSelectionEnabled = true,
            Text = value,
            TextWrapping = TextWrapping.Wrap,
        };
        if (monospace)
        {
            valueText.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono");
        }

        StackPanel field = new() { Spacing = 3 };
        field.Children.Add(new TextBlock
        {
            FontSize = 12,
            Opacity = 0.7,
            Text = label,
        });
        field.Children.Add(valueText);
        return field;
    }

    private void OnRulesResetting(object? sender, EventArgs args)
    {
        _isReplacingRules = true;
        if (RulesList.SelectedItem is RuleDisplayItem selected)
        {
            _selectedRuleKey = GetRuleKey(selected);
        }
    }

    private void OnRulesResetCompleted(object? sender, EventArgs args)
    {
        RuleDisplayItem? replacement = _selectedRuleKey is { } key
            ? Rules.FirstOrDefault(rule => MatchesRuleKey(rule, key))
            : null;
        RulesList.SelectedItem = replacement;
        _isReplacingRules = false;
        _selectedRuleKey = replacement is null ? null : GetRuleKey(replacement);
    }

    private static (int Index, string? Identifier) GetRuleKey(RuleDisplayItem rule) =>
        (rule.ApiIndex ?? rule.Index, rule.Identifier == "--" ? null : rule.Identifier);

    private static bool MatchesRuleKey(
        RuleDisplayItem rule,
        (int Index, string? Identifier) key) =>
        key.Identifier is not null
            ? string.Equals(rule.Identifier, key.Identifier, StringComparison.Ordinal)
            : (rule.ApiIndex ?? rule.Index) == key.Index;

    private void RaiseQueryChanged()
    {
        string filter = RuleFilterBox.SelectedItem is ComboBoxItem item && item.Tag is string tag
            ? tag
            : "all";
        QueryChanged?.Invoke(this, new RuleQueryChangedEventArgs(RuleSearchBox.Text, filter));
    }

    private void UpdateEmptyState()
    {
        bool rulesEmpty = Rules.Count == 0;
        bool filteredEmpty = rulesEmpty &&
            ViewModel is RulesViewModel { IsFilteredEmpty: true };
        RulesEmptyState.StateTitle = filteredEmpty
            ? "No matching rules"
            : "No rules loaded";
        RulesEmptyState.Description = filteredEmpty
            ? "No routing rules match the current search or type filter. Try adjusting them."
            : "Routing rules will appear after they are fetched from the controller.";
        RulesEmptyState.Visibility = rulesEmpty
            ? Visibility.Visible
            : Visibility.Collapsed;
        RuleProvidersEmptyState.Visibility = RuleProviders.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateCommandContext()
    {
        bool showingProviders = RulesPivot.SelectedIndex == 1;
        RuleQueryControls.Visibility = showingProviders ? Visibility.Collapsed : Visibility.Visible;
        RefreshRulesButton.Visibility = showingProviders ? Visibility.Collapsed : Visibility.Visible;
        UpdateRuleProvidersButton.Visibility = showingProviders ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RaiseRuleToggle(RuleDisplayItem rule, bool disabled)
    {
        if (ViewModel is not ViewModelBase { CanStartUserOperation: true })
        {
            return;
        }

        RuleToggleRequested?.Invoke(
            this,
            new RuleToggleRequestedEventArgs(rule.ApiIndex, rule.Identifier, disabled));
    }
}

public sealed class RuleToggleRequestedEventArgs(
    int? index,
    string? identifier,
    bool disabled) : EventArgs
{
    public int? Index { get; } = index;

    public string? Identifier { get; } = identifier == "--" ? null : identifier;

    public bool Disabled { get; } = disabled;
}

public sealed class RuleProviderRequestedEventArgs(string providerName) : EventArgs
{
    public string ProviderName { get; } = providerName;
}

public sealed class RuleQueryChangedEventArgs(string query, string filter) : EventArgs
{
    public string Query { get; } = query;

    public string Filter { get; } = filter;
}
