using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using Zashboard.App.Controls;
using Zashboard.App.Services;
using Zashboard.Core.Clash;

namespace Zashboard.App.ViewModels;

public sealed record LogQuery(string Text, string Level);

public sealed partial class LogsViewModel : ViewModelBase
{
    private string _query = string.Empty;
    private bool _isActive = true;
    private string _level = "all";
    private bool _isPaused;
    private bool _followTail = true;
    private string? _clipboardText;
    private SessionLogEntry[]? _pausedSource;
    private long _pausedDroppedLogCount;
    private int _sourceEntryCount;
    private long _droppedLogCount;

    public LogsViewModel(AppSessionCoordinator coordinator)
        : base(coordinator)
    {
        ClearCommand = new AsyncRelayCommand(ExecuteClearCommandAsync);
        SetQueryCommand = new AsyncRelayCommand<LogQuery>(ExecuteSetQueryCommandAsync);
        SetPausedCommand = new AsyncRelayCommand<bool>(ExecuteSetPausedCommandAsync);
        SetFollowTailCommand = new AsyncRelayCommand<bool>(ExecuteSetFollowTailCommandAsync);
        CopyCommand = new AsyncRelayCommand<IReadOnlyList<LogDisplayItem>>(ExecuteCopyCommandAsync);
        Coordinator.LogsChanged += OnLogsChanged;
        RefreshLogs();
    }

    public BulkObservableCollection<LogDisplayItem> LogEntries { get; } = [];

    public IAsyncRelayCommand ClearCommand { get; }

    public IAsyncRelayCommand<LogQuery> SetQueryCommand { get; }

    public IAsyncRelayCommand<bool> SetPausedCommand { get; }

    public IAsyncRelayCommand<bool> SetFollowTailCommand { get; }

    public IAsyncRelayCommand<IReadOnlyList<LogDisplayItem>> CopyCommand { get; }

    public string Query
    {
        get => _query;
        private set => SetProperty(ref _query, value);
    }

    public string Level
    {
        get => _level;
        private set => SetProperty(ref _level, value);
    }

    public bool IsPaused
    {
        get => _isPaused;
    }

    public bool FollowTail
    {
        get => _followTail;
    }

    public string? ClipboardText
    {
        get => _clipboardText;
        private set => SetProperty(ref _clipboardText, value);
    }

    public int SourceEntryCount
    {
        get => _sourceEntryCount;
        private set => SetProperty(ref _sourceEntryCount, value);
    }

    public long DroppedLogCount
    {
        get => _droppedLogCount;
        private set => SetProperty(ref _droppedLogCount, value);
    }

    public void SetActive(bool isActive)
    {
        if (_isActive == isActive)
        {
            return;
        }

        _isActive = isActive;
        if (isActive)
        {
            RefreshLogs();
        }
    }

    public Task ClearAsync()
    {
        Coordinator.ClearLogs();
        return Task.CompletedTask;
    }

    public Task SetQueryAsync(string? query, string? level)
    {
        Query = query?.Trim() ?? string.Empty;
        Level = NormalizeLevel(level);
        RefreshLogs();
        return Task.CompletedTask;
    }

    public Task SetPausedAsync(bool isPaused)
    {
        if (isPaused == IsPaused)
        {
            return Task.CompletedTask;
        }

        if (isPaused)
        {
            _pausedSource = Coordinator.Logs.ToArray();
            _pausedDroppedLogCount = Coordinator.DroppedLogCount;
            SetViewMode(isPaused: true, followTail: false);
        }
        else
        {
            _pausedSource = null;
            SetViewMode(isPaused: false, followTail: FollowTail);
            RefreshLogs();
        }

        return Task.CompletedTask;
    }

    public Task SetFollowTailAsync(bool followTail)
    {
        if (followTail && IsPaused)
        {
            _pausedSource = null;
        }

        SetViewMode(isPaused: followTail ? false : IsPaused, followTail);
        if (followTail)
        {
            RefreshLogs();
        }

        return Task.CompletedTask;
    }

    public static string BuildClipboardText(IReadOnlyList<LogDisplayItem> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        StringBuilder builder = new();
        foreach (LogDisplayItem entry in entries)
        {
            _ = builder
                .Append(entry.Timestamp.ToLocalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture))
                .Append(' ')
                .Append('[')
                .Append(entry.Level)
                .Append("] ")
                .AppendLine(entry.Message);
        }

        return builder.ToString();
    }

    protected override void DisposeCore()
    {
        Coordinator.LogsChanged -= OnLogsChanged;
    }

    private Task ExecuteClearCommandAsync() => ClearAsync();

    private Task ExecuteSetQueryCommandAsync(LogQuery? query) => query is null
        ? Task.CompletedTask
        : SetQueryAsync(query.Text, query.Level);

    private Task ExecuteSetPausedCommandAsync(bool isPaused) => SetPausedAsync(isPaused);

    private Task ExecuteSetFollowTailCommandAsync(bool followTail) => SetFollowTailAsync(followTail);

    private Task ExecuteCopyCommandAsync(IReadOnlyList<LogDisplayItem>? entries)
    {
        ClipboardText = entries is null ? null : BuildClipboardText(entries);
        return Task.CompletedTask;
    }

    private void OnLogsChanged(object? sender, SessionLogsChangedEventArgs args)
    {
        if (IsPaused)
        {
            if (args.IsReset)
            {
                _pausedSource = [];
                _pausedDroppedLogCount = Coordinator.DroppedLogCount;
                RefreshLogs();
            }

            return;
        }

        if (!_isActive)
        {
            if (args.IsReset)
            {
                RefreshLogs();
            }

            return;
        }

        SourceEntryCount = Coordinator.Logs.Count;
        DroppedLogCount = Coordinator.DroppedLogCount;
        if (!args.IsReset && Query.Length == 0 && Level == "all")
        {
            ApplyIncrementalDelta(
                args.RemovedFromStart,
                args.Added.Select(DisplayModelFactory.Log));
            return;
        }

        if (!args.IsReset)
        {
            ReconcileLiveEntries();
            return;
        }

        RefreshLogs();
    }

    private void RefreshLogs()
    {
        IReadOnlyList<SessionLogEntry> snapshot = IsPaused
            ? _pausedSource ?? []
            : Coordinator.Logs;
        SourceEntryCount = snapshot.Count;
        DroppedLogCount = IsPaused
            ? _pausedDroppedLogCount
            : Coordinator.DroppedLogCount;
        if (!_isActive)
        {
            if (snapshot.Count == 0 && LogEntries.Count > 0)
            {
                LogEntries.ReplaceAll([]);
            }

            return;
        }

        ReplaceCollection(LogEntries, BuildDisplayEntries(snapshot));
    }

    private LogDisplayItem[] BuildDisplayEntries(IReadOnlyList<SessionLogEntry> snapshot)
    {
        IEnumerable<SessionLogEntry> source = snapshot;
        if (Level != "all")
        {
            source = source.Where(entry => MatchesLevel(entry, Level));
        }

        if (Query.Length > 0)
        {
            source = source.Where(entry =>
                entry.Message.Contains(Query, StringComparison.CurrentCultureIgnoreCase) ||
                entry.RawLevel.Contains(Query, StringComparison.CurrentCultureIgnoreCase));
        }

        return source.Select(DisplayModelFactory.Log).ToArray();
    }

    private void ReconcileLiveEntries()
    {
        LogDisplayItem[] items = BuildDisplayEntries(Coordinator.Logs);
        CollectionSynchronizer.ReconcileByKey(
            LogEntries,
            items,
            static item => item.Sequence,
            static (_, _) => { });
    }

    private static bool MatchesLevel(SessionLogEntry entry, string level)
    {
        int minimum = level switch
        {
            "trace" => 0,
            "debug" => 1,
            "info" => 2,
            "warning" => 3,
            "error" => 4,
            _ => int.MinValue,
        };
        int severity = entry.Level switch
        {
            ClashLogLevel.Trace => 0,
            ClashLogLevel.Debug => 1,
            ClashLogLevel.Info => 2,
            ClashLogLevel.Warning => 3,
            ClashLogLevel.Error => 4,
            ClashLogLevel.Fatal => 5,
            ClashLogLevel.Panic => 6,
            _ => int.MinValue,
        };
        return severity >= minimum;
    }

    private void ApplyIncrementalDelta(
        int removeFromStart,
        IEnumerable<LogDisplayItem> addedItems)
    {
        int removeCount = Math.Min(removeFromStart, LogEntries.Count);
        for (int index = 0; index < removeCount; index++)
        {
            LogEntries.RemoveAt(0);
        }

        foreach (LogDisplayItem item in addedItems)
        {
            LogEntries.Add(item);
        }
    }

    private static string NormalizeLevel(string? level) => level switch
    {
        "trace" => "trace",
        "debug" => "debug",
        "info" => "info",
        "warning" => "warning",
        "error" => "error",
        _ => "all",
    };

    private void SetViewMode(bool isPaused, bool followTail)
    {
        bool pausedChanged = _isPaused != isPaused;
        bool followChanged = _followTail != followTail;
        _isPaused = isPaused;
        _followTail = followTail;
        if (pausedChanged)
        {
            OnPropertyChanged(nameof(IsPaused));
        }

        if (followChanged)
        {
            OnPropertyChanged(nameof(FollowTail));
        }
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> destination, IEnumerable<T> source)
    {
        CollectionBatch.Replace(destination, source);
    }
}
