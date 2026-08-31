namespace Zashboard.Core.Capabilities;

public interface ICapabilityRegistry
{
    event EventHandler<CapabilityChangedEventArgs>? Changed;

    CapabilityObservation GetObservation(ClashCapability capability);

    IReadOnlyDictionary<ClashCapability, CapabilityObservation> GetSnapshot();

    void Observe(
        ClashCapability capability,
        CapabilitySupport support,
        CapabilityEvidenceKind evidence,
        DateTimeOffset observedAt,
        string? detail = null);

    void Reset();
}
