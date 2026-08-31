using System.Globalization;
using System.Text;
using Zashboard.Core.Clash;

namespace Zashboard.Core.Normalization;

public static class ClashTypeNormalizer
{
    public static ClashCoreKind ClassifyCore(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return ClashCoreKind.Unknown;
        }

        return ContainsToken(version, "honk")
            ? ClashCoreKind.Honk
            : ClashCoreKind.Mihomo;
    }

    public static ClashProxyKind NormalizeProxyKind(string? value) => NormalizeToken(value) switch
    {
        "direct" => ClashProxyKind.Direct,
        "reject" => ClashProxyKind.Reject,
        "rejectdrop" => ClashProxyKind.RejectDrop,
        "block" => ClashProxyKind.Block,
        "compatible" => ClashProxyKind.Compatible,
        "pass" => ClashProxyKind.Pass,
        "passrule" => ClashProxyKind.PassRule,
        "rematch" => ClashProxyKind.Rematch,
        "dns" => ClashProxyKind.Dns,
        "relay" => ClashProxyKind.Relay,
        "selector" => ClashProxyKind.Selector,
        "fallback" => ClashProxyKind.Fallback,
        "urltest" => ClashProxyKind.UrlTest,
        "smart" => ClashProxyKind.Smart,
        "loadbalance" => ClashProxyKind.LoadBalance,
        _ => ClashProxyKind.Unknown,
    };

    public static ClashMode NormalizeMode(string? value) => NormalizeToken(value) switch
    {
        "rule" => ClashMode.Rule,
        "global" => ClashMode.Global,
        "direct" => ClashMode.Direct,
        "script" => ClashMode.Script,
        _ => ClashMode.Unknown,
    };

    public static ClashLogLevel NormalizeLogLevel(string? value) => NormalizeToken(value) switch
    {
        "trace" => ClashLogLevel.Trace,
        "debug" => ClashLogLevel.Debug,
        "info" => ClashLogLevel.Info,
        "warning" or "warn" => ClashLogLevel.Warning,
        "error" => ClashLogLevel.Error,
        "fatal" => ClashLogLevel.Fatal,
        "panic" => ClashLogLevel.Panic,
        "silent" => ClashLogLevel.Silent,
        _ => ClashLogLevel.Unknown,
    };

    public static ClashNetworkKind NormalizeNetwork(
        string? network,
        string? destinationPort = null,
        string? sniffHost = null)
    {
        string token = NormalizeToken(network);
        if (token == "udp" &&
            (string.Equals(destinationPort, "443", StringComparison.Ordinal) ||
             !string.IsNullOrWhiteSpace(sniffHost)))
        {
            return ClashNetworkKind.Quic;
        }

        return token switch
        {
            "tcp" => ClashNetworkKind.Tcp,
            "udp" => ClashNetworkKind.Udp,
            "quic" => ClashNetworkKind.Quic,
            _ => ClashNetworkKind.Unknown,
        };
    }

    public static bool TryNormalizeTimestamp(string? value, out DateTimeOffset timestamp)
    {
        if (DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
            out timestamp))
        {
            return true;
        }

        timestamp = default;
        return false;
    }

    public static bool TryNormalizeUnixTimestamp(long value, out DateTimeOffset timestamp)
    {
        try
        {
            timestamp = value is >= 100_000_000_000 or <= -100_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(value)
                : DateTimeOffset.FromUnixTimeSeconds(value);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            timestamp = default;
            return false;
        }
    }

    private static string NormalizeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        StringBuilder builder = new(value.Length);
        foreach (char character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static bool ContainsToken(string source, string token)
    {
        int searchFrom = 0;
        while (searchFrom <= source.Length - token.Length)
        {
            int index = source.IndexOf(token, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            int after = index + token.Length;
            bool startsAtBoundary = index == 0 || !IsWordCharacter(source[index - 1]);
            bool endsAtBoundary = after == source.Length || !IsWordCharacter(source[after]);
            if (startsAtBoundary && endsAtBoundary)
            {
                return true;
            }

            searchFrom = index + 1;
        }

        return false;
    }

    private static bool IsWordCharacter(char value) =>
        char.IsLetterOrDigit(value) || value == '_';
}
