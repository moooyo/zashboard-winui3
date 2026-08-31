namespace Zashboard.Core.Clash;

public enum ClashLogLevel
{
    Unknown,
    Trace,
    Debug,
    Info,
    Warning,
    Error,
    Fatal,
    Panic,
    Silent,
}

public sealed record ClashLogMessage
{
    public ClashLogLevel Level { get; init; } = ClashLogLevel.Unknown;

    public string RawLevel { get; init; } = string.Empty;

    public string Payload { get; init; } = string.Empty;
}

public sealed record ClashTrafficSample
{
    public long Down { get; init; }

    public long Up { get; init; }

    public long? DownTotal { get; init; }

    public long? UpTotal { get; init; }
}

public sealed record ClashMemorySample
{
    public long InUse { get; init; }
}
