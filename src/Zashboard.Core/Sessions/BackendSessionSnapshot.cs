using Zashboard.Core.Backends;
using Zashboard.Core.Capabilities;
using Zashboard.Core.Clash;

namespace Zashboard.Core.Sessions;

public sealed record BackendSessionSnapshot
{
    public SessionEpoch Epoch { get; init; }

    public Guid? ProfileId { get; init; }

    public BackendConnectionState State { get; init; } = BackendConnectionState.NoBackend;

    public ClashCoreKind CoreKind { get; init; } = ClashCoreKind.Unknown;

    public string Version { get; init; } = string.Empty;

    public DateTimeOffset StateChangedAt { get; init; }

    public DateTimeOffset? LastSuccessfulContactAt { get; init; }

    public string? StatusDetail { get; init; }

    public IReadOnlyDictionary<ClashCapability, CapabilityObservation> Capabilities { get; init; } =
        new Dictionary<ClashCapability, CapabilityObservation>();

    public bool BelongsTo(SessionEpoch epoch) => Epoch == epoch;

    public static BackendSessionSnapshot NoBackend(SessionEpoch epoch, DateTimeOffset changedAt) => new()
    {
        Epoch = epoch,
        State = BackendConnectionState.NoBackend,
        StateChangedAt = changedAt,
    };
}
