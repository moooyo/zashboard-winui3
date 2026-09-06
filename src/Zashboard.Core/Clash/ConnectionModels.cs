namespace Zashboard.Core.Clash;

public enum ClashNetworkKind
{
    Unknown,
    Tcp,
    Udp,
    Quic,
}

public sealed record ClashConnectionMetadata
{
    public IReadOnlyList<string> DestinationGeoIp { get; init; } = [];

    public string DestinationIp { get; init; } = string.Empty;

    public string DestinationIpAsn { get; init; } = string.Empty;

    public string DestinationPort { get; init; } = string.Empty;

    public string DnsMode { get; init; } = string.Empty;

    public int Dscp { get; init; }

    public string Host { get; init; } = string.Empty;

    public string InboundIp { get; init; } = string.Empty;

    public string InboundName { get; init; } = string.Empty;

    public string InboundPort { get; init; } = string.Empty;

    public string InboundUser { get; init; } = string.Empty;

    public string Network { get; init; } = string.Empty;

    public string Process { get; init; } = string.Empty;

    public string ProcessPath { get; init; } = string.Empty;

    public string RemoteDestination { get; init; } = string.Empty;

    public string SniffHost { get; init; } = string.Empty;

    public IReadOnlyList<string> SourceGeoIp { get; init; } = [];

    public string SourceIp { get; init; } = string.Empty;

    public string SourceIpAsn { get; init; } = string.Empty;

    public string SourcePort { get; init; } = string.Empty;

    public string SpecialProxy { get; init; } = string.Empty;

    public string SpecialRules { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public long Uid { get; init; }

    public string SmartBlock { get; init; } = string.Empty;
}

public sealed record ClashConnection
{
    public string Id { get; init; } = string.Empty;

    public long Download { get; init; }

    public long Upload { get; init; }

    public IReadOnlyList<string> Chains { get; init; } = [];

    public string Rule { get; init; } = string.Empty;

    public string RulePayload { get; init; } = string.Empty;

    public DateTimeOffset? StartedAt { get; init; }

    public string StartValue { get; init; } = string.Empty;

    public ClashConnectionMetadata Metadata { get; init; } = new();
}

public sealed record ConnectionStreamSnapshot
{
    public IReadOnlyList<ClashConnection> Connections { get; init; } = [];

    public long DownloadTotal { get; init; }

    public long UploadTotal { get; init; }

    public long Memory { get; init; }

    public IReadOnlyDictionary<string, ConnectionTransferRate> TransferRates { get; init; } =
        new Dictionary<string, ConnectionTransferRate>(StringComparer.Ordinal);
}

public readonly record struct ConnectionTransferRate(long Download, long Upload);
