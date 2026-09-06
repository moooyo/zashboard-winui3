using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Zashboard.App.Services;

namespace Zashboard.App.ViewModels;

public abstract partial class ViewModelBase : ObservableObject, IDisposable
{
    private int _busyCount;
    private string? _errorMessage;
    private int _disposeState;

    protected ViewModelBase(AppSessionCoordinator coordinator)
    {
        Coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        Coordinator.PropertyChanged += OnCoordinatorPropertyChanged;
    }

    protected AppSessionCoordinator Coordinator { get; }

    public bool IsBusy => _busyCount > 0;

    public bool CanStartUserOperation => !Coordinator.IsUserOperationRunning;

    public string? ErrorMessage
    {
        get => _errorMessage;
        protected set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    protected Task ExecuteAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(action, allowCancellation: true, cancellationToken);

    protected async Task ExecuteAsync(
        Func<CancellationToken, Task> action,
        bool allowCancellation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (IsBusy || Coordinator.IsUserOperationRunning)
        {
            return;
        }

        BeginOperation();
        ErrorMessage = null;
        try
        {
            await Coordinator.RunUserOperationAsync(action, allowCancellation, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            EndOperation();
        }
    }

    protected virtual void HandleCoordinatorPropertyChanged(string? propertyName)
    {
    }

    protected void ClearError() => ErrorMessage = null;

    private void OnCoordinatorPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(AppSessionCoordinator.IsUserOperationRunning))
        {
            OnPropertyChanged(nameof(CanStartUserOperation));
        }

        HandleCoordinatorPropertyChanged(args.PropertyName);
    }

    private void BeginOperation()
    {
        _busyCount++;
        if (_busyCount == 1)
        {
            OnPropertyChanged(nameof(IsBusy));
        }
    }

    private void EndOperation()
    {
        _busyCount = Math.Max(0, _busyCount - 1);
        if (_busyCount == 0)
        {
            OnPropertyChanged(nameof(IsBusy));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0)
        {
            Coordinator.PropertyChanged -= OnCoordinatorPropertyChanged;
            DisposeCore();
        }

        GC.SuppressFinalize(this);
    }

    protected virtual void DisposeCore()
    {
    }
}
