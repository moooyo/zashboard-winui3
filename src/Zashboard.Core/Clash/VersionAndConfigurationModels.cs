namespace Zashboard.Core.Clash;

public enum ClashCoreKind
{
    Unknown,
    Mihomo,
    Honk,
}

public enum ClashMode
{
    Unknown,
    Rule,
    Global,
    Direct,
    Script,
}

public enum CoreUpgradeChannel
{
    Auto,
    Release,
    Alpha,
}

public sealed record ClashVersion
{
    public string Value { get; init; } = string.Empty;

    public ClashCoreKind CoreKind { get; init; } = ClashCoreKind.Unknown;
}

public sealed record ClashTunConfiguration
{
    public bool Enabled { get; init; }
}

public sealed record ClashConfiguration
{
    public int Port { get; init; }

    public int SocksPort { get; init; }

    public int RedirPort { get; init; }

    public int TProxyPort { get; init; }

    public int MixedPort { get; init; }

    public bool AllowLan { get; init; }

    public string BindAddress { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public IReadOnlyList<string> ModeList { get; init; } = [];

    public IReadOnlyList<string> Modes { get; init; } = [];

    public string LogLevel { get; init; } = string.Empty;

    public bool Ipv6 { get; init; }

    public ClashTunConfiguration? Tun { get; init; }
}

public sealed record ClashConfigurationPatch
{
    public int? Port { get; init; }

    public int? SocksPort { get; init; }

    public int? RedirPort { get; init; }

    public int? TProxyPort { get; init; }

    public int? MixedPort { get; init; }

    public bool? AllowLan { get; init; }

    public string? BindAddress { get; init; }

    public string? Mode { get; init; }

    public string? LogLevel { get; init; }

    public bool? Ipv6 { get; init; }

    public bool? TunEnabled { get; init; }
}

public sealed record ClashConfigurationUpdate
{
    public string Path { get; init; } = string.Empty;

    public string Payload { get; init; } = string.Empty;

    public bool Force { get; init; }
}
