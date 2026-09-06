using Zashboard.App.Services;
using Zashboard.App.ViewModels;
using Zashboard.Core.Clash;

namespace Zashboard.App.Logic.Tests;

[TestClass]
public sealed class UserOperationViewModelTests
{
    [TestMethod]
    public async Task RunningControllerOperationAllowsLogBrowsingAndPreventsCompetingCommands()
    {
        await using AppSessionCoordinator coordinator = CreateCoordinator();
        using OperationViewModel operation = new(coordinator);
        using OperationViewModel otherPage = new(coordinator);
        using LogsViewModel logs = new(coordinator);
        List<bool> availability = [];
        otherPage.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ViewModelBase.CanStartUserOperation))
            {
                availability.Add(otherPage.CanStartUserOperation);
            }
        };
        TaskCompletionSource entered = NewCompletion();
        TaskCompletionSource release = NewCompletion();
        Task running = operation.RunAsync(async token =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            Assert.IsFalse(otherPage.CanStartUserOperation);
            bool competingCommandStarted = false;
            await otherPage.RunAsync(_ =>
            {
                competingCommandStarted = true;
                return Task.CompletedTask;
            });
            Assert.IsFalse(competingCommandStarted);

            coordinator.AppendLogsForTest(
                [new ClashLogMessage { Level = ClashLogLevel.Info, RawLevel = "info", Payload = "lookup failed" }],
                DateTimeOffset.UnixEpoch);
            await logs.SetQueryAsync("lookup", "all");
            await logs.SetPausedAsync(true);

            Assert.AreEqual("lookup failed", logs.LogEntries.Single().Message);
            Assert.IsTrue(logs.IsPaused);
            Assert.IsTrue(coordinator.IsUserOperationRunning);
        }
        finally
        {
            release.TrySetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.IsTrue(otherPage.CanStartUserOperation);
        Assert.HasCount(2, availability);
        Assert.IsFalse(availability[0]);
        Assert.IsTrue(availability[1]);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ViewModelForwardsCancellationPolicyAndRestoresAvailability(bool allowCancellation)
    {
        await using AppSessionCoordinator coordinator = CreateCoordinator();
        using OperationViewModel viewModel = new(coordinator);
        TaskCompletionSource entered = NewCompletion();
        TaskCompletionSource release = NewCompletion();
        CancellationToken operationToken = default;
        Task running = viewModel.RunAsync(async token =>
        {
            operationToken = token;
            entered.SetResult();
            await release.Task.WaitAsync(token);
        }, allowCancellation);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            Assert.AreEqual(allowCancellation, coordinator.CanCancelUserOperation);
            coordinator.CancelUserOperation();
            if (allowCancellation)
            {
                await running.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.IsTrue(operationToken.IsCancellationRequested);
            }
            else
            {
                Assert.IsFalse(operationToken.IsCancellationRequested);
                Assert.IsFalse(running.IsCompleted);
            }
        }
        finally
        {
            release.TrySetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.IsFalse(viewModel.IsBusy);
        Assert.IsTrue(viewModel.CanStartUserOperation);
        Assert.IsNull(viewModel.ErrorMessage);
    }

    private static TaskCompletionSource NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static AppSessionCoordinator CreateCoordinator() => new(
        new FakeBackendProfileStore(),
        new FakeBackendCredentialStore(),
        new FakeBackendSessionFactory(),
        new InlineTestDispatcher(),
        new AppSettingsState(),
        TimeProvider.System);

    private sealed partial class OperationViewModel(AppSessionCoordinator coordinator) : ViewModelBase(coordinator)
    {
        public Task RunAsync(Func<CancellationToken, Task> action, bool allowCancellation = true) =>
            ExecuteAsync(action, allowCancellation: allowCancellation);
    }
}
