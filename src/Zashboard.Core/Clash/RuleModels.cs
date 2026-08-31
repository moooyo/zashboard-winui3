namespace Zashboard.Core.Clash;

public sealed record ClashRuleStatistics
{
    public bool Disabled { get; init; }

    public DateTimeOffset? HitAt { get; init; }

    public long HitCount { get; init; }

    public DateTimeOffset? MissAt { get; init; }

    public long MissCount { get; init; }
}

public sealed record ClashRule
{
    public string Type { get; init; } = string.Empty;

    public string Payload { get; init; } = string.Empty;

    public string Proxy { get; init; } = string.Empty;

    public long Size { get; init; }

    public string? Identifier { get; init; }

    public bool? Disabled { get; init; }

    public int? Index { get; init; }

    public ClashRuleStatistics? Statistics { get; init; }
}

public sealed record RuleCatalog
{
    public IReadOnlyList<ClashRule> Rules { get; init; } = [];
}

public sealed record ClashRuleProvider
{
    public string Behavior { get; init; } = string.Empty;

    public string Format { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public long RuleCount { get; init; }

    public string Type { get; init; } = string.Empty;

    public DateTimeOffset? UpdatedAt { get; init; }

    public string VehicleType { get; init; } = string.Empty;
}

public sealed record RuleProviderCatalog
{
    public IReadOnlyDictionary<string, ClashRuleProvider> Providers { get; init; } =
        new Dictionary<string, ClashRuleProvider>(StringComparer.Ordinal);
}
