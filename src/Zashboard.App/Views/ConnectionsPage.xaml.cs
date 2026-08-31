using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Zashboard.App.Controls;

namespace Zashboard.App.Views;

public sealed partial class ConnectionsPage : Page
{
    private const double CompactPaneThreshold = 760;

    private bool _isApplyingViewState;
    private bool _canControlConnections;
    private bool _compactShowingDetails;
    private bool _detailsHadFocusBeforeReset;
    private bool _isCompactLayout;
    private bool _isPaused;
    private bool _isReplacingConnections;
    private bool _listHadFocusBeforeReset;
    private bool _selectionRemovedBeforeRestore;
    private bool _viewportRestorePending;
    private double? _pendingVerticalOffset;
    private string? _displayedConnectionId;
    private string _query = string.Empty;
    private ConnectionDisplayItem? _selectedConnection;
    private string? _selectedConnectionId;
    private ScrollViewer? _connectionsScrollViewer;
    private int _totalConnectionCount;

    public ConnectionsPage()
    {
        InitializeComponent();
        Connections.CollectionChanged += (_, _) =>
        {
            UpdateEmptyState();
            UpdateConnectionStatus();
            UpdateSelectedActionState();
        };
        Connections.Resetting += OnConnectionsResetting;
        Connections.ResetCompleted += OnConnectionsResetCompleted;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        UpdateEmptyState();
        UpdateConnectionStatus();
        UpdateSelectedActionState();
    }

    public event EventHandler? RefreshRequested;

    public event EventHandler? DisconnectAllRequested;

    public event EventHandler<ConnectionRequestedEventArgs>? DisconnectRequested;

    public event EventHandler<ConnectionRequestedEventArgs>? BlockRequested;

    public event EventHandler<SearchRequestedEventArgs>? SearchRequested;

    public event EventHandler<PauseRequestedEventArgs>? PauseRequested;

    public BulkObservableCollection<ConnectionDisplayItem> Connections { get; } = [];

    public object? ViewModel
    {
        get => DataContext;
        set => DataContext = value;
    }

    public void ShowMessage(string title, string message, InfoBarSeverity severity)
    {
        ConnectionsInfoBar.Title = title;
        ConnectionsInfoBar.Message = message;
        ConnectionsInfoBar.Severity = severity;
        AutomationProperties.SetItemStatus(ConnectionsInfoBar, severity.ToString());
        AutomationProperties.SetName(ConnectionsInfoBar, $"{title}. {message}");
        ConnectionsInfoBar.IsOpen = true;
    }

    public void ClearErrorMessage()
    {
        if (ConnectionsInfoBar.Severity == InfoBarSeverity.Error &&
            string.Equals(ConnectionsInfoBar.Title, "Operation failed", StringComparison.Ordinal))
        {
            ConnectionsInfoBar.IsOpen = false;
        }
    }

    public void ApplyConnectionState(int totalConnectionCount, bool canControlConnections)
    {
        _totalConnectionCount = Math.Max(0, totalConnectionCount);
        _canControlConnections = canControlConnections;
        UpdateCloseAllState();
        UpdateEmptyState();
        UpdateConnectionStatus();
        UpdateSelectedActionState();
    }

    public void ApplyViewState(string query, bool isPaused)
    {
        _isApplyingViewState = true;
        try
        {
            _query = query;
            _isPaused = isPaused;
            if (!string.Equals(ConnectionSearchBox.Text, query, StringComparison.Ordinal))
            {
                ConnectionSearchBox.Text = query;
            }

            if (PauseUpdatesButton.IsChecked != isPaused)
            {
                PauseUpdatesButton.IsChecked = isPaused;
            }
        }
        finally
        {
            _isApplyingViewState = false;
        }

        UpdateEmptyState();
        UpdateConnectionStatus();
        UpdateCloseAllState();
        UpdateSelectedActionState();
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs args)
    {
        _isPaused = false;
        _isApplyingViewState = true;
        try
        {
            PauseUpdatesButton.IsChecked = false;
        }
        finally
        {
            _isApplyingViewState = false;
        }

        UpdateConnectionStatus();
        UpdateCloseAllState();
        UpdateSelectedActionState();
        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void OnDisconnectAllClicked(object sender, RoutedEventArgs args)
    {
        if (_canControlConnections && !_isPaused && _totalConnectionCount > 0 && await ConfirmAsync(
            "Close all active connections?",
            "Every active connection reported by the controller will be terminated.",
            "Close all"))
        {
            DisconnectAllRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnDisconnectSelectedClicked(object sender, RoutedEventArgs args)
    {
        if (DisconnectSelectedButton.IsEnabled &&
            ConnectionsList.SelectedItem is ConnectionDisplayItem connection)
        {
            DisconnectRequested?.Invoke(this, new ConnectionRequestedEventArgs(connection.Id));
        }
    }

    private void OnBlockSelectedClicked(object sender, RoutedEventArgs args)
    {
        if (BlockConnectionButton.IsEnabled &&
            ConnectionsList.SelectedItem is ConnectionDisplayItem { CanBlock: true } connection)
        {
            BlockRequested?.Invoke(this, new ConnectionRequestedEventArgs(connection.Id));
        }
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (!_isApplyingViewState &&
            args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            _query = sender.Text.Trim();
            UpdateEmptyState();
            UpdateConnectionStatus();
            SearchRequested?.Invoke(this, new SearchRequestedEventArgs(sender.Text));
        }
    }

    private void OnPauseToggled(object sender, RoutedEventArgs args)
    {
        _isPaused = PauseUpdatesButton.IsChecked == true;
        UpdateConnectionStatus();
        UpdateCloseAllState();
        UpdateSelectedActionState();
        if (_isApplyingViewState)
        {
            return;
        }

        PauseRequested?.Invoke(this, new PauseRequestedEventArgs(_isPaused));
    }

    private void OnConnectionSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_isReplacingConnections)
        {
            return;
        }

        if (ConnectionsList.SelectedItem is not ConnectionDisplayItem connection)
        {
            _selectedConnectionId = null;
            ClearConnectionDetails();
            return;
        }

        _selectedConnectionId = connection.Id;
        ShowConnectionDetails(connection);
    }

    private void OnConnectionItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not ConnectionDisplayItem connection)
        {
            return;
        }

        ConnectionsList.SelectedItem = connection;
        if (_isCompactLayout)
        {
            ShowCompactDetails();
        }
    }

    private void OnCompactDetailsBackClicked(object sender, RoutedEventArgs args)
    {
        _compactShowingDetails = false;
        UpdateMasterDetailLayout();
        _ = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (ConnectionsList.SelectedItem is not null)
            {
                ConnectionsList.ScrollIntoView(ConnectionsList.SelectedItem);
            }

            _ = ConnectionsList.Focus(FocusState.Programmatic);
        });
    }

    private void OnFocusSearchAccelerator(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_isCompactLayout && _compactShowingDetails)
        {
            _compactShowingDetails = false;
            UpdateMasterDetailLayout();
        }

        args.Handled = ConnectionSearchBox.Focus(FocusState.Keyboard);
    }

    private void OnMasterDetailSizeChanged(object sender, SizeChangedEventArgs args)
    {
        bool useCompactLayout = args.NewSize.Width < CompactPaneThreshold;
        if (_isCompactLayout == useCompactLayout)
        {
            return;
        }

        DependencyObject? focusedElement = XamlRoot is null
            ? null
            : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        bool detailsHadFocus = IsDescendantOf(focusedElement, ConnectionDetailsPane);
        bool compactHeaderHadFocus = IsDescendantOf(focusedElement, CompactDetailsHeader);
        _isCompactLayout = useCompactLayout;
        _compactShowingDetails = useCompactLayout &&
            ConnectionsList.SelectedItem is ConnectionDisplayItem &&
            detailsHadFocus;
        UpdateMasterDetailLayout();
        if (!useCompactLayout && compactHeaderHadFocus)
        {
            _ = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                _ = ConnectionDetailsScrollViewer.Focus(FocusState.Programmatic));
        }
    }

    private void ShowConnectionDetails(ConnectionDisplayItem connection)
    {
        TrackSelectedConnection(connection);
        bool selectionChanged = !string.Equals(
            _displayedConnectionId,
            connection.Id,
            StringComparison.Ordinal);
        _displayedConnectionId = connection.Id;
        DetailHostText.Text = connection.Host;
        DetailNetworkText.Text = connection.Network;
        DetailRuleText.Text = connection.Rule;
        DetailChainText.Text = connection.Chain;
        DetailStartedText.Text = connection.StartedAt;
        DetailDownloadText.Text = connection.DownloadTotal;
        DetailUploadText.Text = connection.UploadTotal;
        ApplyBlockAction(connection.CanBlock);
        ConnectionDetailsEmptyState.Visibility = Visibility.Collapsed;
        ConnectionDetailsContent.Visibility = Visibility.Visible;
        UpdateSelectedActionState();
        if (selectionChanged)
        {
            _ = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                ConnectionDetailsScrollViewer.ChangeView(null, 0, null, true));
        }
    }

    private void ClearConnectionDetails()
    {
        TrackSelectedConnection(null);
        _displayedConnectionId = null;
        ConnectionDetailsEmptyState.Visibility = Visibility.Visible;
        ConnectionDetailsContent.Visibility = Visibility.Collapsed;
        _compactShowingDetails = false;
        ApplyBlockAction(false);
        UpdateSelectedActionState();
        UpdateMasterDetailLayout();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (ConnectionsList.SelectedItem is ConnectionDisplayItem connection)
        {
            ShowConnectionDetails(connection);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        TrackSelectedConnection(null);
    }

    private void TrackSelectedConnection(ConnectionDisplayItem? connection)
    {
        if (ReferenceEquals(_selectedConnection, connection))
        {
            return;
        }

        if (_selectedConnection is not null)
        {
            _selectedConnection.PropertyChanged -= OnSelectedConnectionPropertyChanged;
        }

        _selectedConnection = connection;
        if (_selectedConnection is not null)
        {
            _selectedConnection.PropertyChanged += OnSelectedConnectionPropertyChanged;
        }
    }

    private void OnSelectedConnectionPropertyChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, _selectedConnection) || _selectedConnection is null)
        {
            return;
        }

        ConnectionDisplayItem connection = _selectedConnection;
        switch (args.PropertyName)
        {
            case nameof(ConnectionDisplayItem.Host):
                DetailHostText.Text = connection.Host;
                break;
            case nameof(ConnectionDisplayItem.Network):
                DetailNetworkText.Text = connection.Network;
                break;
            case nameof(ConnectionDisplayItem.Rule):
                DetailRuleText.Text = connection.Rule;
                break;
            case nameof(ConnectionDisplayItem.Chain):
                DetailChainText.Text = connection.Chain;
                break;
            case nameof(ConnectionDisplayItem.StartedAt):
                DetailStartedText.Text = connection.StartedAt;
                break;
            case nameof(ConnectionDisplayItem.DownloadTotal):
                DetailDownloadText.Text = connection.DownloadTotal;
                break;
            case nameof(ConnectionDisplayItem.UploadTotal):
                DetailUploadText.Text = connection.UploadTotal;
                break;
            case nameof(ConnectionDisplayItem.CanBlock):
                ApplyBlockAction(connection.CanBlock);
                UpdateSelectedActionState();
                break;
        }
    }

    private void ApplyBlockAction(bool canBlock)
    {
        BlockActionColumn.Width = canBlock ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        BlockActionGutterColumn.Width = canBlock ? new GridLength(8) : new GridLength(0);
        BlockConnectionButton.Visibility = canBlock ? Visibility.Visible : Visibility.Collapsed;
        BlockConnectionButton.IsEnabled = canBlock && CanActOnSelectedConnection();
    }

    private void UpdateEmptyState()
    {
        bool isEmpty = Connections.Count == 0;
        bool hasFilterMiss = isEmpty &&
            _totalConnectionCount > 0 &&
            !string.IsNullOrWhiteSpace(_query);
        ConnectionsEmptyState.StateTitle = hasFilterMiss
            ? "No matching connections"
            : _isPaused ? "No connections in this snapshot" : "No active connections";
        ConnectionsEmptyState.Description = hasFilterMiss
            ? "No connection in this snapshot matches the search. Search includes host, process, path, rule, payload, and proxy chain."
            : _isPaused
                ? "The paused snapshot contains no active connections. Show the latest snapshot to resume updates."
                : "Active sessions will appear when the connection stream is available.";
        ConnectionsEmptyState.Visibility = isEmpty
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateCloseAllState();
    }

    private void UpdateCloseAllState()
    {
        CloseAllConnectionsButton.IsEnabled = _canControlConnections &&
            !_isPaused &&
            _totalConnectionCount > 0;
        AutomationProperties.SetName(
            CloseAllConnectionsButton,
            _totalConnectionCount == 1
                ? "Close all 1 active connection"
                : $"Close all {_totalConnectionCount} active connections");
        string closeAllHelpText = _isPaused
            ? "Resume live updates before closing connections."
            : _canControlConnections
                ? "Close every active connection, including connections hidden by the current search."
                : "Connection control is unavailable for the current controller state.";
        AutomationProperties.SetHelpText(CloseAllConnectionsButton, closeAllHelpText);
        ToolTipService.SetToolTip(CloseAllConnectionsButton, closeAllHelpText);
    }

    private void UpdateConnectionStatus()
    {
        int visibleCount = Connections.Count;
        string totalText = _isPaused
            ? _totalConnectionCount == 1
                ? "1 connection in paused snapshot"
                : $"{_totalConnectionCount} connections in paused snapshot"
            : _totalConnectionCount == 1
                ? "1 active connection"
                : $"{_totalConnectionCount} active connections";
        ConnectionStatusText.Text = !string.IsNullOrWhiteSpace(_query) &&
            visibleCount != _totalConnectionCount
            ? $"{visibleCount} of {totalText}"
            : totalText;
        AutomationProperties.SetName(ConnectionStatusText, ConnectionStatusText.Text);
        ToolTipService.SetToolTip(ConnectionStatusText, ConnectionStatusText.Text);
        AutomationProperties.SetItemStatus(
            PauseUpdatesButton,
            _isPaused ? "Updates paused; snapshot actions disabled" : "Updates active");
        ShowLatestButton.IsEnabled = _isPaused;
    }

    private void UpdateSelectedActionState()
    {
        bool canAct = CanActOnSelectedConnection();
        DisconnectSelectedButton.IsEnabled = canAct;
        string closeHelpText = _isPaused
            ? "Resume live updates before closing this connection."
            : !_canControlConnections
                ? "Connection control is unavailable for the current controller state."
                : ConnectionsList.SelectedItem is ConnectionDisplayItem
                    ? "Close the selected active connection."
                    : "Select an active connection to close it.";
        AutomationProperties.SetHelpText(DisconnectSelectedButton, closeHelpText);
        ToolTipService.SetToolTip(DisconnectSelectedButton, closeHelpText);

        if (ConnectionsList.SelectedItem is ConnectionDisplayItem selected)
        {
            BlockConnectionButton.IsEnabled = selected.CanBlock && canAct;
        }
        else
        {
            BlockConnectionButton.IsEnabled = false;
        }
    }

    private bool CanActOnSelectedConnection() =>
        !_isPaused &&
        _canControlConnections &&
        ConnectionsList.SelectedItem is ConnectionDisplayItem selected &&
        Connections.Any(connection => string.Equals(
            connection.Id,
            selected.Id,
            StringComparison.Ordinal));

    private void ShowCompactDetails()
    {
        if (!_isCompactLayout || ConnectionsList.SelectedItem is not ConnectionDisplayItem)
        {
            return;
        }

        _compactShowingDetails = true;
        UpdateMasterDetailLayout();
        _ = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            _ = CompactDetailsBackButton.Focus(FocusState.Programmatic));
    }

    private void UpdateMasterDetailLayout()
    {
        if (_isCompactLayout)
        {
            ConnectionsColumn.Width = new GridLength(1, GridUnitType.Star);
            PaneGutterColumn.Width = new GridLength(0);
            DetailsColumn.Width = new GridLength(0);
            Grid.SetColumn(ConnectionDetailsPane, 0);
            Grid.SetColumnSpan(ConnectionDetailsPane, 3);
            ConnectionsPane.Visibility = _compactShowingDetails
                ? Visibility.Collapsed
                : Visibility.Visible;
            ConnectionDetailsPane.Visibility = _compactShowingDetails
                ? Visibility.Visible
                : Visibility.Collapsed;
            CompactDetailsHeader.Visibility = _compactShowingDetails
                ? Visibility.Visible
                : Visibility.Collapsed;
            return;
        }

        ConnectionsColumn.Width = new GridLength(1, GridUnitType.Star);
        PaneGutterColumn.Width = new GridLength(12);
        DetailsColumn.Width = new GridLength(320);
        Grid.SetColumn(ConnectionDetailsPane, 2);
        Grid.SetColumnSpan(ConnectionDetailsPane, 1);
        ConnectionsPane.Visibility = Visibility.Visible;
        ConnectionDetailsPane.Visibility = Visibility.Visible;
        CompactDetailsHeader.Visibility = Visibility.Collapsed;
    }

    private void OnConnectionsResetting(object? sender, EventArgs args)
    {
        _isReplacingConnections = true;
        DependencyObject? focusedElement = XamlRoot is null
            ? null
            : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        _listHadFocusBeforeReset |= IsDescendantOf(focusedElement, ConnectionsList);
        _detailsHadFocusBeforeReset |= IsDescendantOf(focusedElement, ConnectionDetailsPane);
        _connectionsScrollViewer ??= FindDescendant<ScrollViewer>(ConnectionsList);
        if (_connectionsScrollViewer is not null)
        {
            _pendingVerticalOffset = _connectionsScrollViewer.VerticalOffset;
        }

        if (ConnectionsList.SelectedItem is ConnectionDisplayItem selected)
        {
            _selectedConnectionId = selected.Id;
        }
    }

    private void OnConnectionsResetCompleted(object? sender, EventArgs args)
    {
        ConnectionDisplayItem? replacement = _selectedConnectionId is null
            ? null
            : Connections.FirstOrDefault(connection => string.Equals(
                connection.Id,
                _selectedConnectionId,
                StringComparison.Ordinal));
        ConnectionsList.SelectedItem = replacement;
        _isReplacingConnections = false;
        if (replacement is null)
        {
            _selectedConnectionId = null;
            ClearConnectionDetails();
        }
        else
        {
            ShowConnectionDetails(replacement);
        }

        RequestViewportRestore(replacement is null);
    }

    private void RequestViewportRestore(bool selectionWasRemoved)
    {
        _selectionRemovedBeforeRestore |= selectionWasRemoved;
        if (_viewportRestorePending)
        {
            return;
        }

        _viewportRestorePending = true;
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _viewportRestorePending = false;
            _connectionsScrollViewer ??= FindDescendant<ScrollViewer>(ConnectionsList);
            if (_pendingVerticalOffset is double offset &&
                _connectionsScrollViewer is not null &&
                ConnectionsPane.Visibility == Visibility.Visible)
            {
                _ = _connectionsScrollViewer.ChangeView(null, offset, null, true);
            }

            _pendingVerticalOffset = null;
            bool restoreListFocus = _listHadFocusBeforeReset ||
                (_detailsHadFocusBeforeReset && _selectionRemovedBeforeRestore && Connections.Count > 0);
            bool restoreSearchFocus = _detailsHadFocusBeforeReset &&
                _selectionRemovedBeforeRestore &&
                Connections.Count == 0;
            _listHadFocusBeforeReset = false;
            _detailsHadFocusBeforeReset = false;
            _selectionRemovedBeforeRestore = false;
            if (restoreListFocus)
            {
                _ = ConnectionsList.Focus(FocusState.Programmatic);
            }
            else if (restoreSearchFocus)
            {
                _ = ConnectionSearchBox.Focus(FocusState.Programmatic);
            }
        }))
        {
            _viewportRestorePending = false;
            _pendingVerticalOffset = null;
            _listHadFocusBeforeReset = false;
            _detailsHadFocusBeforeReset = false;
            _selectionRemovedBeforeRestore = false;
        }
    }

    private static T? FindDescendant<T>(DependencyObject parent)
        where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            T? descendant = FindDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static bool IsDescendantOf(DependencyObject? element, DependencyObject ancestor)
    {
        for (DependencyObject? current = element;
            current is not null;
            current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
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

public sealed class ConnectionRequestedEventArgs(string connectionId) : EventArgs
{
    public string ConnectionId { get; } = connectionId;
}

public sealed class SearchRequestedEventArgs(string query) : EventArgs
{
    public string Query { get; } = query;
}

public sealed class PauseRequestedEventArgs(bool isPaused) : EventArgs
{
    public bool IsPaused { get; } = isPaused;
}
