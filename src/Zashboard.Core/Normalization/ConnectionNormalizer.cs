using Zashboard.Core.Clash;

namespace Zashboard.Core.Normalization;

public static class ConnectionNormalizer
{
    public static string GetHost(ClashConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ClashConnectionMetadata metadata = connection.Metadata;
        string host = FirstNonEmpty(metadata.Host, metadata.SniffHost, metadata.DestinationIp);
        if (string.IsNullOrEmpty(host))
        {
            return string.Empty;
        }

        if (string.IsNullOrEmpty(metadata.DestinationPort))
        {
            return host;
        }

        return host.Contains(':', StringComparison.Ordinal)
            ? $"[{host}]:{metadata.DestinationPort}"
            : $"{host}:{metadata.DestinationPort}";
    }

    public static string GetProcessName(ClashConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (!string.IsNullOrWhiteSpace(connection.Metadata.Process))
        {
            return connection.Metadata.Process;
        }

        if (string.IsNullOrWhiteSpace(connection.Metadata.ProcessPath))
        {
            return string.Empty;
        }

        string path = connection.Metadata.ProcessPath;
        int separator = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return separator >= 0 && separator < path.Length - 1
            ? path[(separator + 1)..]
            : path;
    }

    public static string GetRuleDisplayName(ClashConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return string.IsNullOrWhiteSpace(connection.RulePayload)
            ? connection.Rule
            : $"{connection.Rule}: {connection.RulePayload}";
    }

    public static ConnectionTransferRate CalculateTransferRate(
        ClashConnection current,
        ClashConnection? previous)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (previous is null || !StringComparer.Ordinal.Equals(current.Id, previous.Id))
        {
            return default;
        }

        return new ConnectionTransferRate(
            Math.Max(0, current.Download - previous.Download),
            Math.Max(0, current.Upload - previous.Upload));
    }

    public static ConnectionTransferRate CalculateTransferRate(
        ClashConnection current,
        ClashConnection? previous,
        TimeSpan elapsed)
    {
        ConnectionTransferRate transferred = CalculateTransferRate(current, previous);
        if (elapsed <= TimeSpan.Zero)
        {
            return default;
        }

        return new ConnectionTransferRate(
            ToPerSecond(transferred.Download, elapsed),
            ToPerSecond(transferred.Upload, elapsed));
    }

    private static string FirstNonEmpty(params ReadOnlySpan<string> values)
    {
        foreach (string value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static long ToPerSecond(long value, TimeSpan elapsed)
    {
        double rate = value / elapsed.TotalSeconds;
        return !double.IsFinite(rate) || rate >= long.MaxValue
            ? long.MaxValue
            : Math.Max(0, (long)Math.Round(rate, MidpointRounding.AwayFromZero));
    }
}
