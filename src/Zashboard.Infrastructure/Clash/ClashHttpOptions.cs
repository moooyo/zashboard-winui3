namespace Zashboard.Infrastructure.Clash;

public sealed class ClashHttpOptions
{
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    internal void Validate() =>
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(OperationTimeout, TimeSpan.Zero);
}
