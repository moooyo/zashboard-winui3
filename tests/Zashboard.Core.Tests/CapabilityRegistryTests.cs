using Zashboard.Core.Capabilities;

namespace Zashboard.Core.Tests;

[TestClass]
public sealed class CapabilityRegistryTests
{
    [TestMethod]
    public void NewRegistryContainsEveryCapabilityAsUnknown()
    {
        CapabilityRegistry registry = new();

        IReadOnlyDictionary<ClashCapability, CapabilityObservation> snapshot = registry.GetSnapshot();

        Assert.HasCount(Enum.GetValues<ClashCapability>().Length, snapshot);
        foreach (ClashCapability capability in Enum.GetValues<ClashCapability>())
        {
            Assert.AreEqual(CapabilityObservation.Unknown, snapshot[capability]);
        }
    }

    [TestMethod]
    public void ObserveStoresEvidenceAndRaisesChangedOnceForIdenticalObservation()
    {
        CapabilityRegistry registry = new();
        List<CapabilityChangedEventArgs> changes = [];
        registry.Changed += (_, args) => changes.Add(args);
        DateTimeOffset observedAt = new(2026, 8, 30, 1, 2, 3, TimeSpan.Zero);

        registry.Observe(
            ClashCapability.CoreRestart,
            CapabilitySupport.Supported,
            CapabilityEvidenceKind.SuccessfulCall,
            observedAt,
            "HTTP 204");
        registry.Observe(
            ClashCapability.CoreRestart,
            CapabilitySupport.Supported,
            CapabilityEvidenceKind.SuccessfulCall,
            observedAt,
            "HTTP 204");

        CapabilityObservation observation = registry.GetObservation(ClashCapability.CoreRestart);
        Assert.AreEqual(CapabilitySupport.Supported, observation.Support);
        Assert.AreEqual(CapabilityEvidenceKind.SuccessfulCall, observation.Evidence);
        Assert.AreEqual(observedAt, observation.ObservedAt);
        Assert.AreEqual("HTTP 204", observation.Detail);
        Assert.HasCount(1, changes);
        Assert.AreEqual(CapabilityObservation.Unknown, changes[0].Previous);
        Assert.AreEqual(observation, changes[0].Current);
    }

    [TestMethod]
    public void ResetReturnsObservedCapabilitiesToUnknownAndRaisesChanged()
    {
        CapabilityRegistry registry = new();
        registry.Observe(
            ClashCapability.SettingsStorage,
            CapabilitySupport.Unsupported,
            CapabilityEvidenceKind.EndpointNotFound,
            DateTimeOffset.UtcNow);
        List<CapabilityChangedEventArgs> changes = [];
        registry.Changed += (_, args) => changes.Add(args);

        registry.Reset();

        Assert.AreEqual(
            CapabilityObservation.Unknown,
            registry.GetObservation(ClashCapability.SettingsStorage));
        Assert.HasCount(1, changes);
        Assert.AreEqual(ClashCapability.SettingsStorage, changes[0].Capability);
        Assert.AreEqual(CapabilitySupport.Unsupported, changes[0].Previous.Support);
        Assert.AreEqual(CapabilityObservation.Unknown, changes[0].Current);
    }

    [TestMethod]
    public void ObserveRejectsContradictoryEvidence()
    {
        CapabilityRegistry registry = new();

        Assert.ThrowsExactly<ArgumentException>(() => registry.Observe(
            ClashCapability.CoreUpgrade,
            CapabilitySupport.Unknown,
            CapabilityEvidenceKind.CoreHint,
            DateTimeOffset.UtcNow));
        Assert.ThrowsExactly<ArgumentException>(() => registry.Observe(
            ClashCapability.CoreUpgrade,
            CapabilitySupport.Supported,
            CapabilityEvidenceKind.EndpointNotFound,
            DateTimeOffset.UtcNow));
        Assert.ThrowsExactly<ArgumentException>(() => registry.Observe(
            ClashCapability.CoreUpgrade,
            CapabilitySupport.Unsupported,
            CapabilityEvidenceKind.SuccessfulCall,
            DateTimeOffset.UtcNow));
    }

    [TestMethod]
    public void SnapshotIsIsolatedFromLaterObservations()
    {
        CapabilityRegistry registry = new();
        IReadOnlyDictionary<ClashCapability, CapabilityObservation> before =
            registry.GetSnapshot();

        registry.Observe(
            ClashCapability.RuntimeStatistics,
            CapabilitySupport.Supported,
            CapabilityEvidenceKind.SuccessfulCall,
            DateTimeOffset.UtcNow);

        Assert.AreEqual(
            CapabilityObservation.Unknown,
            before[ClashCapability.RuntimeStatistics]);
        Assert.AreEqual(
            CapabilitySupport.Supported,
            registry.GetSnapshot()[ClashCapability.RuntimeStatistics].Support);
    }

    [TestMethod]
    public void ConcurrentObservationSupportsReentrantSnapshotReads()
    {
        CapabilityRegistry registry = new();
        ClashCapability[] capabilities = Enum.GetValues<ClashCapability>();
        int eventCount = 0;
        registry.Changed += (_, _) =>
        {
            Assert.HasCount(capabilities.Length, registry.GetSnapshot());
            _ = Interlocked.Increment(ref eventCount);
        };

        Parallel.For(0, 512, index =>
        {
            ClashCapability capability = capabilities[index % capabilities.Length];
            CapabilitySupport support = index % 2 == 0
                ? CapabilitySupport.Supported
                : CapabilitySupport.Unsupported;
            CapabilityEvidenceKind evidence = support == CapabilitySupport.Supported
                ? CapabilityEvidenceKind.SuccessfulCall
                : CapabilityEvidenceKind.MethodNotAllowed;
            registry.Observe(
                capability,
                support,
                evidence,
                DateTimeOffset.UnixEpoch.AddTicks(index));
            Assert.HasCount(capabilities.Length, registry.GetSnapshot());
        });

        Assert.IsGreaterThan(0, eventCount);
    }
}
