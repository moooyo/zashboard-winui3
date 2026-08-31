namespace Zashboard.Infrastructure.Sessions;

public sealed class BackendSessionOptions
{
    public TimeSpan InitializationTimeout { get; init; } = TimeSpan.FromSeconds(10);

    internal void Validate() =>
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(InitializationTimeout, TimeSpan.Zero);
}
