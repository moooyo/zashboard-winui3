namespace Zashboard.Core.Capabilities;

public sealed record CapabilityObservation(
    CapabilitySupport Support,
    CapabilityEvidenceKind Evidence,
    DateTimeOffset ObservedAt,
    string? Detail = null)
{
    public static CapabilityObservation Unknown { get; } = new(
        CapabilitySupport.Unknown,
        CapabilityEvidenceKind.None,
        DateTimeOffset.MinValue);
}

public sealed class CapabilityChangedEventArgs : EventArgs
{
    public CapabilityChangedEventArgs(
        ClashCapability capability,
        CapabilityObservation previous,
        CapabilityObservation current)
    {
        Capability = capability;
        Previous = previous;
        Current = current;
    }

    public ClashCapability Capability { get; }

    public CapabilityObservation Previous { get; }

    public CapabilityObservation Current { get; }
}
