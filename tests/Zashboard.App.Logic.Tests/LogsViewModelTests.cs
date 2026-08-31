using System.Collections.Specialized;
using Zashboard.App.Controls;
using Zashboard.App.Services;
using Zashboard.App.ViewModels;
using Zashboard.Core.Clash;

namespace Zashboard.App.Logic.Tests;

[TestClass]
public sealed class LogsViewModelTests
{
    [TestMethod]
    public async Task LiveRetentionDeltaUsesIncrementalCollectionNotifications()
    {
        await using AppSessionCoordinator coordinator = CreateCoordinator(logBufferSize: 500);
        ClashLogMessage[] initial = Enumerable.Range(0, 500)
            .Select(index => Message($"entry {index}"))
            .ToArray();
        coordinator.AppendLogsForTest(initial, DateTimeOffset.UnixEpoch);
        using LogsViewModel viewModel = new(coordinator);
        List<NotifyCollectionChangedAction> actions = [];
        viewModel.LogEntries.CollectionChanged += (_, args) => actions.Add(args.Action);

        coordinator.AppendLogsForTest(
            [Message("newest")],
            DateTimeOffset.UnixEpoch.AddSeconds(1));

        CollectionAssert.Contains(actions, NotifyCollectionChangedAction.Remove);
        CollectionAssert.Contains(actions, NotifyCollectionChangedAction.Add);
        CollectionAssert.DoesNotContain(actions, NotifyCollectionChangedAction.Reset);
        Assert.HasCount(500, viewModel.LogEntries);
        Assert.AreEqual("newest", viewModel.LogEntries[^1].Message);
    }

    [TestMethod]
    public async Task FilteredLiveUpdatesDoNotResetTheVisibleCollection()
    {
        await using AppSessionCoordinator coordinator = CreateCoordinator();
        coordinator.AppendLogsForTest([Message("match first")], DateTimeOffset.UnixEpoch);
        using LogsViewModel viewModel = new(coordinator);
        await viewModel.SetQueryAsync("match", "all");
        List<NotifyCollectionChangedAction> actions = [];
        viewModel.LogEntries.CollectionChanged += (_, args) => actions.Add(args.Action);

        coordinator.AppendLogsForTest(
            [Message("match second")],
            DateTimeOffset.UnixEpoch.AddSeconds(1));

        CollectionAssert.Contains(actions, NotifyCollectionChangedAction.Add);
        CollectionAssert.DoesNotContain(actions, NotifyCollectionChangedAction.Reset);
        Assert.HasCount(2, viewModel.LogEntries);
    }

    [TestMethod]
    public async Task FilteringWhilePausedUsesFrozenSourceAfterLiveBufferChanges()
    {
        await using AppSessionCoordinator coordinator = CreateCoordinator(logBufferSize: 500);
        ClashLogMessage[] initial = Enumerable.Range(0, 500)
            .Select(index => Message(index == 0 ? "first" : $"filler {index}"))
            .ToArray();
        coordinator.AppendLogsForTest(
            initial,
            DateTimeOffset.UnixEpoch);
        using LogsViewModel viewModel = new(coordinator);

        await viewModel.SetPausedAsync(true);
        coordinator.AppendLogsForTest(
            [Message("third")],
            DateTimeOffset.UnixEpoch.AddSeconds(1));

        await viewModel.SetQueryAsync("first", "all");

        Assert.IsTrue(viewModel.IsPaused);
        Assert.IsFalse(viewModel.FollowTail);
        Assert.AreEqual(500, viewModel.SourceEntryCount);
        Assert.HasCount(1, viewModel.LogEntries);
        Assert.AreEqual("first", viewModel.LogEntries[0].Message);

        await viewModel.SetQueryAsync("third", "all");
        Assert.IsEmpty(viewModel.LogEntries);

        await viewModel.SetPausedAsync(false);
        Assert.IsFalse(viewModel.IsPaused);
        Assert.HasCount(1, viewModel.LogEntries);
        Assert.AreEqual("third", viewModel.LogEntries[0].Message);
    }

    [TestMethod]
    public async Task EnablingFollowResumesUpdatesAndRefreshesTheLiveSource()
    {
        await using AppSessionCoordinator coordinator = CreateCoordinator();
        coordinator.AppendLogsForTest([Message("before pause")], DateTimeOffset.UnixEpoch);
        using LogsViewModel viewModel = new(coordinator);
        await viewModel.SetPausedAsync(true);
        coordinator.AppendLogsForTest(
            [Message("while paused")],
            DateTimeOffset.UnixEpoch.AddSeconds(1));

        await viewModel.SetFollowTailAsync(true);

        Assert.IsFalse(viewModel.IsPaused);
        Assert.IsTrue(viewModel.FollowTail);
        Assert.HasCount(2, viewModel.LogEntries);
        Assert.AreEqual("while paused", viewModel.LogEntries[1].Message);
    }

    [TestMethod]
    public async Task ClearingWhilePausedClearsTheSnapshotAndKeepsLaterLogsHidden()
    {
        await using AppSessionCoordinator coordinator = CreateCoordinator();
        coordinator.AppendLogsForTest([Message("old")], DateTimeOffset.UnixEpoch);
        using LogsViewModel viewModel = new(coordinator);
        await viewModel.SetPausedAsync(true);

        await viewModel.ClearAsync();
        coordinator.AppendLogsForTest(
            [Message("new")],
            DateTimeOffset.UnixEpoch.AddSeconds(1));
        await viewModel.SetQueryAsync("new", "all");

        Assert.IsTrue(viewModel.IsPaused);
        Assert.AreEqual(0, viewModel.SourceEntryCount);
        Assert.IsEmpty(viewModel.LogEntries);

        await viewModel.SetPausedAsync(false);
        Assert.HasCount(1, viewModel.LogEntries);
        Assert.AreEqual("new", viewModel.LogEntries[0].Message);
    }

    [TestMethod]
    public async Task DroppedCountFreezesWithThePausedSnapshotAndRetentionDoesNotIncreaseIt()
    {
        await using AppSessionCoordinator coordinator = CreateCoordinator(logBufferSize: 500);
        ClashLogMessage[] initial = Enumerable.Range(0, 500)
            .Select(index => Message($"entry {index}"))
            .ToArray();
        coordinator.AppendLogsForTest(
            initial,
            DateTimeOffset.UnixEpoch,
            droppedBeforeDisplay: 3);
        using LogsViewModel viewModel = new(coordinator);
        await viewModel.SetPausedAsync(true);

        coordinator.AppendLogsForTest(
            [Message("three")],
            DateTimeOffset.UnixEpoch.AddSeconds(1),
            droppedBeforeDisplay: 2);
        await viewModel.SetQueryAsync(null, "all");

        Assert.AreEqual(5L, coordinator.DroppedLogCount);
        Assert.AreEqual(3L, viewModel.DroppedLogCount);

        await viewModel.SetPausedAsync(false);
        Assert.AreEqual(5L, viewModel.DroppedLogCount);
        Assert.AreEqual(500, viewModel.SourceEntryCount);
    }

    [TestMethod]
    public async Task DuplicateMessagesReceiveDistinctStableDisplayIdentities()
    {
        await using AppSessionCoordinator coordinator = CreateCoordinator();
        coordinator.AppendLogsForTest(
            [Message("duplicate"), Message("duplicate")],
            DateTimeOffset.UnixEpoch);
        using LogsViewModel viewModel = new(coordinator);

        Assert.HasCount(2, viewModel.LogEntries);
        Assert.AreNotEqual(viewModel.LogEntries[0].Sequence, viewModel.LogEntries[1].Sequence);
        Assert.AreNotEqual(viewModel.LogEntries[0], viewModel.LogEntries[1]);

        long secondSequence = viewModel.LogEntries[1].Sequence;
        await viewModel.SetQueryAsync("duplicate", "all");
        Assert.AreEqual(secondSequence, viewModel.LogEntries[1].Sequence);
    }

    [TestMethod]
    public void ClipboardTextPreservesCompleteLongMultilineMessagesInDisplayOrder()
    {
        string longMessage = $"{new string('x', 4096)}{Environment.NewLine}stack trace line";
        LogDisplayItem[] entries =
        [
            new(1, DateTimeOffset.UnixEpoch, "Info", longMessage),
            new(2, DateTimeOffset.UnixEpoch.AddSeconds(1), "Error", "last"),
        ];

        string text = LogsViewModel.BuildClipboardText(entries);

        string expected =
            $"{entries[0].Timestamp.ToLocalTime():O} [Info] {longMessage}{Environment.NewLine}" +
            $"{entries[1].Timestamp.ToLocalTime():O} [Error] last{Environment.NewLine}";
        Assert.AreEqual(expected, text);
    }

    private static AppSessionCoordinator CreateCoordinator(int logBufferSize = 5_000) => new(
        new FakeBackendProfileStore(),
        new FakeBackendCredentialStore(),
        new FakeBackendSessionFactory(),
        new InlineTestDispatcher(),
        new AppSettingsState { LogBufferSize = logBufferSize },
        TimeProvider.System);

    private static ClashLogMessage Message(string payload) => new()
    {
        Level = ClashLogLevel.Info,
        RawLevel = "info",
        Payload = payload,
    };
}
