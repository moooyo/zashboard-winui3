using System.Collections.ObjectModel;

namespace Zashboard.Core.Capabilities;

public sealed class CapabilityRegistry : ICapabilityRegistry
{
    private static readonly ClashCapability[] AllCapabilities =
    [
        ClashCapability.ConfigurationPatch,
        ClashCapability.ConfigurationReload,
        ClashCapability.ConfigurationUpdate,
        ClashCapability.GeoDataUpdate,
        ClashCapability.CoreUpgrade,
        ClashCapability.CoreRestart,
        ClashCapability.DashboardUpgrade,
        ClashCapability.SettingsStorage,
        ClashCapability.IndependentLatencyHistory,
        ClashCapability.ProviderProxyHealthCheck,
        ClashCapability.ProviderProxyUpdate,
        ClashCapability.RuleDisableByIndex,
        ClashCapability.RuleDisableByIdentifier,
        ClashCapability.SmartWeights,
        ClashCapability.SmartWeightReset,
        ClashCapability.SmartConnectionBlock,
        ClashCapability.RuntimeStatistics,
        ClashCapability.TraceLogLevel,
        ClashCapability.SilentLogLevel,
    ];

    private readonly Lock _gate = new();
    private readonly Dictionary<ClashCapability, CapabilityObservation> _observations = [];

    public CapabilityRegistry()
    {
        ResetCore();
    }

    public event EventHandler<CapabilityChangedEventArgs>? Changed;

    public CapabilityObservation GetObservation(ClashCapability capability)
    {
        lock (_gate)
        {
            return _observations.TryGetValue(capability, out CapabilityObservation? observation)
                ? observation
                : CapabilityObservation.Unknown;
        }
    }

    public IReadOnlyDictionary<ClashCapability, CapabilityObservation> GetSnapshot()
    {
        lock (_gate)
        {
            Dictionary<ClashCapability, CapabilityObservation> copy = new(_observations);
            return new ReadOnlyDictionary<ClashCapability, CapabilityObservation>(copy);
        }
    }

    public void Observe(
        ClashCapability capability,
        CapabilitySupport support,
        CapabilityEvidenceKind evidence,
        DateTimeOffset observedAt,
        string? detail = null)
    {
        ValidateObservation(support, evidence);

        CapabilityObservation current = new(support, evidence, observedAt, detail);
        CapabilityObservation previous;

        lock (_gate)
        {
            previous = _observations.TryGetValue(capability, out CapabilityObservation? value)
                ? value
                : CapabilityObservation.Unknown;

            if (previous == current)
            {
                return;
            }

            _observations[capability] = current;
        }

        Changed?.Invoke(this, new CapabilityChangedEventArgs(capability, previous, current));
    }

    public void Reset()
    {
        List<CapabilityChangedEventArgs> changes = [];

        lock (_gate)
        {
            foreach (ClashCapability capability in AllCapabilities)
            {
                CapabilityObservation previous = _observations.TryGetValue(
                    capability,
                    out CapabilityObservation? value)
                    ? value
                    : CapabilityObservation.Unknown;

                _observations[capability] = CapabilityObservation.Unknown;
                if (previous != CapabilityObservation.Unknown)
                {
                    changes.Add(new CapabilityChangedEventArgs(
                        capability,
                        previous,
                        CapabilityObservation.Unknown));
                }
            }
        }

        foreach (CapabilityChangedEventArgs change in changes)
        {
            Changed?.Invoke(this, change);
        }
    }

    private void ResetCore()
    {
        foreach (ClashCapability capability in AllCapabilities)
        {
            _observations.Add(capability, CapabilityObservation.Unknown);
        }
    }

    private static void ValidateObservation(
        CapabilitySupport support,
        CapabilityEvidenceKind evidence)
    {
        if (support == CapabilitySupport.Unknown && evidence != CapabilityEvidenceKind.None)
        {
            throw new ArgumentException("Unknown capability support cannot carry evidence.", nameof(evidence));
        }

        if (support != CapabilitySupport.Unknown && evidence == CapabilityEvidenceKind.None)
        {
            throw new ArgumentException("A known capability state must carry evidence.", nameof(evidence));
        }

        bool unsupportedEvidence = evidence is
            CapabilityEvidenceKind.BadRequest or
            CapabilityEvidenceKind.EndpointNotFound or
            CapabilityEvidenceKind.MethodNotAllowed;

        if (support == CapabilitySupport.Supported && unsupportedEvidence)
        {
            throw new ArgumentException("Unsupported endpoint evidence cannot mark a capability as supported.", nameof(evidence));
        }

        if (support == CapabilitySupport.Unsupported && evidence == CapabilityEvidenceKind.SuccessfulCall)
        {
            throw new ArgumentException("A successful call cannot mark a capability as unsupported.", nameof(evidence));
        }
    }
}
