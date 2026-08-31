namespace Zashboard.Core.Clash;

public sealed record DnsQueryRequest
{
    public DnsQueryRequest(string name, string type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        Name = name.Trim();
        Type = type.Trim().ToUpperInvariant();
    }

    public string Name { get; }

    public string Type { get; }
}

public sealed record DnsQuestion
{
    public string Name { get; init; } = string.Empty;

    public int Type { get; init; }

    public int Class { get; init; }
}

public sealed record DnsAnswer
{
    public int Ttl { get; init; }

    public string Data { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public int Type { get; init; }
}

public sealed record DnsQueryResult
{
    public bool AuthenticatedData { get; init; }

    public bool CheckingDisabled { get; init; }

    public bool RecursionAvailable { get; init; }

    public bool RecursionDesired { get; init; }

    public bool Truncated { get; init; }

    public int Status { get; init; }

    public IReadOnlyList<DnsQuestion> Questions { get; init; } = [];

    public IReadOnlyList<DnsAnswer> Answers { get; init; } = [];
}
