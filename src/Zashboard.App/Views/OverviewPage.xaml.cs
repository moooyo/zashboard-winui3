using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Zashboard.App.Controls;
using Zashboard.App.ViewModels;

namespace Zashboard.App.Views;

public sealed partial class OverviewPage : Page
{
    private const double CompactRuntimeSummaryThreshold = 760;
    private const double CompactTelemetryChartsThreshold = 820;

    private bool _isApplyingControlState;

    public OverviewPage()
    {
        InitializeComponent();
        RecentConnections.CollectionChanged += (_, _) => UpdateEmptyState();
        UpdateEmptyState();
    }

    public event EventHandler? RefreshRequested;

    public event EventHandler? OpenConnectionsRequested;

    public event EventHandler? RefreshHonkStatisticsRequested;

    public event EventHandler<ModeChangedEventArgs>? ModeChanged;

    public event EventHandler<TunChangedEventArgs>? TunChanged;

    public ObservableCollection<RecentConnectionDisplayItem> RecentConnections { get; } = [];

    public ObservableCollection<string> ModeOptions { get; } = new BulkObservableCollection<string>();

    public object? ViewModel
    {
        get => DataContext;
        set => DataContext = value;
    }

    public void SetSessionSummary(
        string state,
        string connectionCount,
        string memory,
        string coreVersion,
        string statusDetail,
        InfoBarSeverity statusSeverity,
        Brush stateBrush,
        bool isOnline,
        bool canRefresh)
    {
        SessionStateText.Text = state;
        SessionStateIndicator.Fill = stateBrush;
        AutomationProperties.SetName(SessionStatePanel, $"Controller status: {state}");
        AutomationProperties.SetItemStatus(SessionStatePanel, statusDetail);
        ConnectionCountText.Text = connectionCount;
        MemoryValueText.Text = memory;
        CoreVersionText.Text = coreVersion;
        ToolTipService.SetToolTip(CoreVersionText, coreVersion);
        AutomationProperties.SetName(CoreVersionText, $"Core version: {coreVersion}");
        SessionStatusInfoBar.Title = state;
        SessionStatusInfoBar.Message = statusDetail;
        SessionStatusInfoBar.Severity = statusSeverity;
        AutomationProperties.SetItemStatus(SessionStatusInfoBar, statusSeverity.ToString());
        AutomationProperties.SetName(SessionStatusInfoBar, $"{state}. {statusDetail}");
        SessionStatusInfoBar.IsOpen = !isOnline;
        RefreshOverviewButton.IsEnabled = canRefresh;
    }

    internal void SetTrafficHistory(
        IReadOnlyList<TrafficHistoryPoint> trafficHistory,
        string downloadRate,
        string uploadRate)
    {
        ArgumentNullException.ThrowIfNull(trafficHistory);
        TrafficTrendChart.SetSeries(
            trafficHistory.Select(static point => (double)point.Download).ToArray(),
            trafficHistory.Select(static point => (double)point.Upload).ToArray(),
            $"Download {downloadRate}; upload {uploadRate}",
            formatPerSecond: true);
    }

    internal void SetMemoryHistory(
        IReadOnlyList<MemoryHistoryPoint> memoryHistory,
        string memory)
    {
        ArgumentNullException.ThrowIfNull(memoryHistory);
        MemoryTrendChart.SetSeries(
            memoryHistory.Select(static point => (double)point.InUse).ToArray(),
            null,
            $"Current {memory}",
            formatPerSecond: false);
    }

    public void ApplyControllerState(
        string? mode,
        bool tunEnabled,
        bool canChangeMode,
        bool canChangeTun)
    {
        _isApplyingControlState = true;
        try
        {
            ModeComboBox.SelectedItem = ModeOptions.FirstOrDefault(option => string.Equals(
                option,
                mode,
                StringComparison.OrdinalIgnoreCase));
            ModeComboBox.IsEnabled = canChangeMode;
            TunToggle.IsOn = tunEnabled;
            TunToggle.IsEnabled = canChangeTun;
        }
        finally
        {
            _isApplyingControlState = false;
        }
    }

    public void SetTrafficRates(
        string downloadRate,
        string uploadRate,
        string downloadTotal,
        string uploadTotal)
    {
        DownloadRateIndicator.Value = downloadRate;
        UploadRateIndicator.Value = uploadRate;
        DownloadRateIndicator.SecondaryValue = downloadTotal;
        UploadRateIndicator.SecondaryValue = uploadTotal;
        AutomationProperties.SetName(
            DownloadRateIndicator,
            $"Download: {downloadRate}. Total: {downloadTotal}.");
        AutomationProperties.SetName(
            UploadRateIndicator,
            $"Upload: {uploadRate}. Total: {uploadTotal}.");
    }

    public void SetHonkRuntimeStatistics(
        bool isVisible,
        bool canRefresh,
        string status,
        string totalConnections,
        string activeConnections,
        string upload,
        string download,
        string errors)
    {
        HonkStatisticsPanel.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        HonkStatisticsGutter.Height = isVisible ? new GridLength(12) : new GridLength(0);
        RefreshHonkStatisticsButton.IsEnabled = canRefresh;
        HonkStatisticsStatusText.Text = status;
        HonkTotalConnectionsText.Text = totalConnections;
        HonkActiveConnectionsText.Text = activeConnections;
        HonkUploadText.Text = upload;
        HonkDownloadText.Text = download;
        HonkErrorsText.Text = errors;
    }

    public void ShowMessage(string title, string message, InfoBarSeverity severity)
    {
        OperationInfoBar.Title = title;
        OperationInfoBar.Message = message;
        OperationInfoBar.Severity = severity;
        AutomationProperties.SetItemStatus(OperationInfoBar, severity.ToString());
        AutomationProperties.SetName(OperationInfoBar, $"{title}. {message}");
        OperationInfoBar.IsOpen = true;
    }

    public void ClearErrorMessage()
    {
        if (OperationInfoBar.Severity == InfoBarSeverity.Error &&
            string.Equals(OperationInfoBar.Title, "Operation failed", StringComparison.Ordinal))
        {
            OperationInfoBar.IsOpen = false;
        }
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs args)
    {
        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnOpenConnectionsClicked(object sender, RoutedEventArgs args)
    {
        OpenConnectionsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnRefreshHonkStatisticsClicked(object sender, RoutedEventArgs args)
    {
        RefreshHonkStatisticsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnModeSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_isApplyingControlState && ModeComboBox.SelectedItem is string mode)
        {
            ModeChanged?.Invoke(this, new ModeChangedEventArgs(mode));
        }
    }

    private void OnTunToggled(object sender, RoutedEventArgs args)
    {
        if (!_isApplyingControlState)
        {
            TunChanged?.Invoke(this, new TunChangedEventArgs(TunToggle.IsOn));
        }
    }

    private void OnRuntimeSummarySizeChanged(object sender, SizeChangedEventArgs args)
    {
        bool useCompactLayout = args.NewSize.Width < CompactRuntimeSummaryThreshold;
        Grid.SetRow(MemorySummary, useCompactLayout ? 2 : 0);
        Grid.SetColumn(MemorySummary, useCompactLayout ? 0 : 3);
        Grid.SetRow(CoreSummary, useCompactLayout ? 2 : 0);
        Grid.SetColumn(CoreSummary, useCompactLayout ? 1 : 4);
        Grid.SetColumnSpan(CoreSummary, useCompactLayout ? 4 : 1);
        CompactSummaryGutter.Height = useCompactLayout
            ? new GridLength(12)
            : new GridLength(0);
        CompactSummaryRow.Height = useCompactLayout
            ? GridLength.Auto
            : new GridLength(0);
    }

    private void OnTelemetryChartsSizeChanged(object sender, SizeChangedEventArgs args)
    {
        bool useCompactLayout = args.NewSize.Width < CompactTelemetryChartsThreshold;
        TrafficChartColumn.Width = new GridLength(1, GridUnitType.Star);
        TelemetryChartGutterColumn.Width = useCompactLayout
            ? new GridLength(0)
            : new GridLength(12);
        MemoryChartColumn.Width = useCompactLayout
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        TelemetryChartGutterRow.Height = useCompactLayout
            ? new GridLength(12)
            : new GridLength(0);
        MemoryChartRow.Height = useCompactLayout
            ? GridLength.Auto
            : new GridLength(0);
        Grid.SetRow(MemoryTrendChart, useCompactLayout ? 2 : 0);
        Grid.SetColumn(MemoryTrendChart, useCompactLayout ? 0 : 2);
    }

    private void UpdateEmptyState()
    {
        RecentConnectionsEmptyState.Visibility = RecentConnections.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}

public sealed class ModeChangedEventArgs(string mode) : EventArgs
{
    public string Mode { get; } = mode;
}

public sealed class TunChangedEventArgs(bool isEnabled) : EventArgs
{
    public bool IsEnabled { get; } = isEnabled;
}
