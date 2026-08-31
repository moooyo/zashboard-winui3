namespace Zashboard.Core.Clash;

public sealed record SmartNodeRank
{
    public string Name { get; init; } = string.Empty;

    public string Rank { get; init; } = string.Empty;

    public double Weight { get; init; }
}

public sealed record SmartWeights
{
    public string Message { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, IReadOnlyList<SmartNodeRank>> Weights { get; init; } =
        new Dictionary<string, IReadOnlyList<SmartNodeRank>>(StringComparer.Ordinal);
}

public sealed record HonkOutboundStatistics
{
    public string Name { get; init; } = string.Empty;

    public long TotalConnections { get; init; }

    public long ActiveConnections { get; init; }

    public long Upload { get; init; }

    public long Download { get; init; }

    public long Errors { get; init; }
}

public sealed record HonkRuntimeStatistics
{
    public IReadOnlyList<HonkOutboundStatistics> Outbounds { get; init; } = [];
}

public sealed record DashboardStorage
{
    public IReadOnlyDictionary<string, string> Values { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
