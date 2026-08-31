namespace Zashboard.App.Services;

public interface IUiDispatcher
{
    bool HasThreadAccess { get; }

    ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default);
}

public sealed class SynchronizationContextUiDispatcher : IUiDispatcher
{
    private readonly SynchronizationContext _context;

    public SynchronizationContextUiDispatcher(SynchronizationContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public bool HasThreadAccess => ReferenceEquals(SynchronizationContext.Current, _context);

    public static SynchronizationContextUiDispatcher CaptureCurrent()
    {
        SynchronizationContext context = SynchronizationContext.Current
            ?? throw new InvalidOperationException(
                "The application dispatcher must be created on the WinUI thread.");
        return new SynchronizationContextUiDispatcher(context);
    }

    public ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(cancellationToken);
        }

        if (HasThreadAccess)
        {
            action();
            return ValueTask.CompletedTask;
        }

        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _context.Post(
            static state => ((DispatchWorkItem)state!).Run(),
            new DispatchWorkItem(action, completion, cancellationToken));
        return new ValueTask(completion.Task);
    }

    private sealed class DispatchWorkItem(
        Action action,
        TaskCompletionSource completion,
        CancellationToken cancellationToken)
    {
        public void Run()
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                action();
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }
    }
}
