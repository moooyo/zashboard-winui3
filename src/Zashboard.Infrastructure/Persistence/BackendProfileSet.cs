using Zashboard.Core.Backends;

namespace Zashboard.Infrastructure.Persistence;

public sealed record BackendProfileSet
{
    public IReadOnlyList<BackendProfile> Profiles { get; init; } = [];

    public Guid? ActiveProfileId { get; init; }
}
