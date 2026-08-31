using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Zashboard.App.Controls;

namespace Zashboard.App.Views;

public sealed partial class LogsPage : Page
{
    private bool _isInitializing = true;
    private bool _isApplyingViewState;
    private bool _isReplacingLogs;
    private bool _isFollowScrollPending;
    private BulkObservableCollection<LogDisplayItem>? _logEntries;
    private LogDisplayItem[] _selectedEntries = [];
    private int _sourceEntryCount;
    private long _droppedLogCount;

    public LogsPage()
    {
        InitializeComponent();
        _isInitializing = false;
        UpdateToggleStatus();
        UpdateEmptyState();
    }

    public event EventHandler? ClearRequested;

    public event EventHandler<LogQueryChangedEventArgs>? QueryChanged;

    public event EventHandler<LogToggleRequestedEventArgs>? PauseRequested;

    public event EventHandler<LogToggleRequestedEventArgs>? FollowTailChanged;

    public event EventHandler<LogCopyRequestedEventArgs>? CopyRequested;

    public object? ViewModel
    {
        get => DataContext;
        set => DataContext = value;
    }

    public void ShowMessage(string title, string message, InfoBarSeverity severity)
    {
        LogsInfoBar.Title = title;
        LogsInfoBar.Message = message;
        LogsInfoBar.Severity = severity;
        AutomationProperties.SetItemStatus(LogsInfoBar, severity.ToString());
        AutomationProperties.SetName(LogsInfoBar, $"{title}. {message}");
        LogsInfoBar.IsOpen = true;
    }

    public void ClearErrorMessage()
    {
        if (LogsInfoBar.Severity == InfoBarSeverity.Error &&
            string.Equals(LogsInfoBar.Title, "Operation failed", StringComparison.Ordinal))
        {
            LogsInfoBar.IsOpen = false;
        }
    }

    public void ApplyViewState(
        string query,
        string level,
        bool isPaused,
        bool followTail,
        int sourceEntryCount,
        long droppedLogCount)
    {
        bool announceFirstDrop = _droppedLogCount == 0 && droppedLogCount > 0;
        _sourceEntryCount = Math.Max(0, sourceEntryCount);
        _droppedLogCount = Math.Max(0, droppedLogCount);
        _isApplyingViewState = true;
        try
        {
            LogSearchBox.Text = query;
            LogLevelFilterBox.SelectedItem = LogLevelFilterBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag as string,
                    level,
                    StringComparison.Ordinal)) ?? LogLevelFilterBox.Items[0];
            PauseLogsButton.IsChecked = isPaused;
            FollowTailButton.IsChecked = followTail;
        }
        finally
        {
            _isApplyingViewState = false;
        }

        UpdateToggleStatus();
        UpdateEmptyState();
        if (announceFirstDrop)
        {
            AnnounceStatus(
                $"{FormatCount(_droppedLogCount)} log entries were dropped before display.");
        }
    }

    public void AttachLogEntries(BulkObservableCollection<LogDisplayItem> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        DetachLogEntries();
        _logEntries = entries;
        _logEntries.CollectionChanged += OnLogEntriesCollectionChanged;
        _logEntries.Resetting += OnLogsResetting;
        _logEntries.ResetCompleted += OnLogsResetCompleted;
        LogsList.ItemsSource = _logEntries;
        UpdateEmptyState();
        RequestFollowTailScroll();
    }

    public void DetachLogEntries()
    {
        if (_logEntries is null)
        {
            return;
        }

        _logEntries.CollectionChanged -= OnLogEntriesCollectionChanged;
        _logEntries.Resetting -= OnLogsResetting;
        _logEntries.ResetCompleted -= OnLogsResetCompleted;
        LogsList.ItemsSource = null;
        _logEntries = null;
    }

    private void OnClearClicked(object sender, RoutedEventArgs args)
    {
        ClearRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCopyClicked(object sender, RoutedEventArgs args)
    {
        LogDisplayItem[] selectedEntries = LogsList.SelectedItems
            .OfType<LogDisplayItem>()
            .ToArray();

        if (selectedEntries.Length > 0)
        {
            CopyRequested?.Invoke(this, new LogCopyRequestedEventArgs(selectedEntries));
        }
    }

    private async void OnInspectClicked(object sender, RoutedEventArgs args)
    {
        if (GetSingleSelectedEntry() is { } entry)
        {
            await ShowLogDetailsAsync(entry);
        }
    }

    private async void OnLogDoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (GetSingleSelectedEntry() is { } entry)
        {
            await ShowLogDetailsAsync(entry);
            args.Handled = true;
        }
    }

    private void OnPauseToggled(object sender, RoutedEventArgs args)
    {
        if (_isInitializing)
        {
            return;
        }

        if (_isApplyingViewState)
        {
            UpdateToggleStatus();
            return;
        }

        bool isPaused = PauseLogsButton.IsChecked == true;
        if (isPaused && FollowTailButton.IsChecked == true)
        {
            ApplyToggleState(() => FollowTailButton.IsChecked = false);
        }

        UpdateToggleStatus();
        AnnounceStatus(isPaused
            ? $"Log updates paused. {FormatCount(_sourceEntryCount)} entries frozen. Follow disabled."
            : "Log updates resumed. Follow remains disabled until enabled.");
        PauseRequested?.Invoke(this, new LogToggleRequestedEventArgs(isPaused));
    }

    private void OnFollowTailToggled(object sender, RoutedEventArgs args)
    {
        if (_isInitializing)
        {
            return;
        }

        if (_isApplyingViewState)
        {
            UpdateToggleStatus();
            return;
        }

        bool followTail = FollowTailButton.IsChecked == true;
        bool resumedFromPause = followTail && PauseLogsButton.IsChecked == true;
        if (resumedFromPause)
        {
            ApplyToggleState(() => PauseLogsButton.IsChecked = false);
        }

        if (followTail && LogsList.SelectedItems.Count > 0)
        {
            LogsList.SelectedItems.Clear();
        }

        UpdateToggleStatus();
        RequestFollowTailScroll();
        AnnounceStatus(followTail
            ? resumedFromPause
                ? "Following the newest log entry. Log updates resumed."
                : "Following the newest log entry."
            : "Follow disabled. Live updates will not move the view.");
        FollowTailChanged?.Invoke(this, new LogToggleRequestedEventArgs(followTail));
    }

    private void OnLogSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (!_isApplyingViewState &&
            args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            RaiseQueryChanged();
        }
    }

    private void OnLogLevelChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_isInitializing && !_isApplyingViewState)
        {
            RaiseQueryChanged();
        }
    }

    private void OnLogSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_isReplacingLogs)
        {
            return;
        }

        _selectedEntries = LogsList.SelectedItems.OfType<LogDisplayItem>().ToArray();
        if (_selectedEntries.Length > 0 && PauseLogsButton.IsChecked != true)
        {
            PauseLogsButton.IsChecked = true;
        }

        UpdateCopySelectionState();
    }

    private void UpdateCopySelectionState()
    {
        int selectedCount = LogsList.SelectedItems.Count;
        CopyLogsButton.IsEnabled = selectedCount > 0;
        InspectLogButton.IsEnabled = selectedCount == 1;
        AutomationProperties.SetName(
            CopyLogsButton,
            selectedCount switch
            {
                0 => "Copy selected log entries",
                1 => "Copy 1 selected log entry",
                _ => $"Copy {selectedCount} selected log entries",
            });
        AutomationProperties.SetName(
            InspectLogButton,
            selectedCount == 1 ? "Inspect selected log entry" : "Inspect one selected log entry");
    }

    private void OnLogsResetting(object? sender, EventArgs args)
    {
        _isReplacingLogs = true;
        _selectedEntries = LogsList.SelectedItems.OfType<LogDisplayItem>().ToArray();
    }

    private void OnLogsResetCompleted(object? sender, EventArgs args)
    {
        LogsList.SelectedItems.Clear();
        if (_selectedEntries.Length == 0)
        {
            _isReplacingLogs = false;
            UpdateCopySelectionState();
            return;
        }

        HashSet<long> remaining = _selectedEntries
            .Select(static entry => entry.Sequence)
            .ToHashSet();

        foreach (LogDisplayItem entry in _logEntries ?? Enumerable.Empty<LogDisplayItem>())
        {
            if (remaining.Remove(entry.Sequence))
            {
                LogsList.SelectedItems.Add(entry);
            }
        }

        _selectedEntries = LogsList.SelectedItems.OfType<LogDisplayItem>().ToArray();
        _isReplacingLogs = false;
        UpdateCopySelectionState();
    }

    private void OnFocusSearchAccelerator(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = LogSearchBox.Focus(FocusState.Keyboard);
    }

    private void OnLogCollectionChanged()
    {
        UpdateEmptyState();
        RequestFollowTailScroll();
    }

    private void RequestFollowTailScroll()
    {
        if (PauseLogsButton.IsChecked != true &&
            FollowTailButton.IsChecked == true &&
            _selectedEntries.Length == 0 &&
            _logEntries is { Count: > 0 } &&
            !_isFollowScrollPending)
        {
            _isFollowScrollPending = true;
            if (!DispatcherQueue.TryEnqueue(() =>
            {
                _isFollowScrollPending = false;
                if (PauseLogsButton.IsChecked != true &&
                    FollowTailButton.IsChecked == true &&
                    _selectedEntries.Length == 0 &&
                    _logEntries is { Count: > 0 } entries)
                {
                    LogsList.ScrollIntoView(entries[^1]);
                }
            }))
            {
                _isFollowScrollPending = false;
            }
        }
    }

    private void OnLogEntriesCollectionChanged(
        object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs args) =>
        OnLogCollectionChanged();

    private void RaiseQueryChanged()
    {
        string level = LogLevelFilterBox.SelectedItem is ComboBoxItem item && item.Tag is string tag
            ? tag
            : "all";
        QueryChanged?.Invoke(this, new LogQueryChangedEventArgs(LogSearchBox.Text, level));
    }

    private void UpdateEmptyState()
    {
        bool isEmpty = _logEntries is null || _logEntries.Count == 0;
        LogsEmptyState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        if (!isEmpty)
        {
            return;
        }

        if (_sourceEntryCount > 0)
        {
            LogsEmptyState.StateTitle = "No matching log messages";
            LogsEmptyState.Description = "Try changing the search text or minimum log level.";
        }
        else if (PauseLogsButton.IsChecked == true)
        {
            LogsEmptyState.StateTitle = "Paused snapshot is empty";
            LogsEmptyState.Description = "Resume updates to show new controller log messages.";
        }
        else
        {
            LogsEmptyState.StateTitle = "No log messages";
            LogsEmptyState.Description = "Log messages will appear while a controller stream is connected.";
        }
    }

    private void UpdateToggleStatus()
    {
        bool isPaused = PauseLogsButton.IsChecked == true;
        bool followTail = FollowTailButton.IsChecked == true;
        AutomationProperties.SetItemStatus(
            PauseLogsButton,
            isPaused ? "Updates paused" : "Updates active");
        AutomationProperties.SetItemStatus(
            FollowTailButton,
            followTail ? "Following newest entry" : "Follow disabled");

        string status = isPaused
            ? $"Paused - {FormatCount(_sourceEntryCount)} entries frozen"
            : followTail
                ? $"Live - following newest - {FormatCount(_sourceEntryCount)} retained"
                : $"Live - follow off - {FormatCount(_sourceEntryCount)} retained";
        if (_droppedLogCount > 0)
        {
            status += $" - {FormatCount(_droppedLogCount)} dropped before display";
        }

        LogsStatusText.Text = status;
        AutomationProperties.SetName(LogsStatusText, status);
        AutomationProperties.SetHelpText(
            PauseLogsButton,
            isPaused
                ? "Resume display updates. Follow remains off until enabled."
                : "Freeze the currently retained log entries.");
        AutomationProperties.SetHelpText(
            FollowTailButton,
            isPaused
                ? "Resume updates and keep the newest log entry in view."
                : "Keep the newest log entry in view.");
        UpdateEmptyState();
    }

    private LogDisplayItem? GetSingleSelectedEntry() =>
        LogsList.SelectedItems.Count == 1
            ? LogsList.SelectedItems[0] as LogDisplayItem
            : null;

    private async Task ShowLogDetailsAsync(LogDisplayItem entry)
    {
        TextBlock message = new()
        {
            IsTextSelectionEnabled = true,
            Text = entry.Message,
            TextWrapping = TextWrapping.Wrap,
        };
        ScrollViewer messageScroller = new()
        {
            Content = message,
            MaxHeight = 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        StackPanel content = new()
        {
            MaxWidth = 640,
            Spacing = 12,
        };
        content.Children.Add(new TextBlock
        {
            Text = $"{entry.Timestamp.ToLocalTime():O}  [{entry.Level}]",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(messageScroller);

        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = "Log entry details",
            Content = content,
            PrimaryButtonText = "Copy entry",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            CopyRequested?.Invoke(this, new LogCopyRequestedEventArgs([entry]));
            AnnounceStatus("Log entry copied to the clipboard.");
        }
    }

    private void ApplyToggleState(Action update)
    {
        _isApplyingViewState = true;
        try
        {
            update();
        }
        finally
        {
            _isApplyingViewState = false;
        }
    }

    private void AnnounceStatus(string message)
    {
        AutomationPeer? peer = FrameworkElementAutomationPeer.FromElement(LogsStatusText) ??
            FrameworkElementAutomationPeer.CreatePeerForElement(LogsStatusText);
        peer?.RaiseNotificationEvent(
            AutomationNotificationKind.ActionCompleted,
            AutomationNotificationProcessing.ImportantMostRecent,
            message,
            "LogsStatus");
    }

    private static string FormatCount(long count) => count.ToString(
        "N0",
        System.Globalization.CultureInfo.CurrentCulture);
}

public sealed class LogQueryChangedEventArgs(string query, string level) : EventArgs
{
    public string Query { get; } = query;

    public string Level { get; } = level;
}

public sealed class LogToggleRequestedEventArgs(bool isEnabled) : EventArgs
{
    public bool IsEnabled { get; } = isEnabled;
}

public sealed class LogCopyRequestedEventArgs(IReadOnlyList<LogDisplayItem> entries) : EventArgs
{
    public IReadOnlyList<LogDisplayItem> Entries { get; } = entries;
}
