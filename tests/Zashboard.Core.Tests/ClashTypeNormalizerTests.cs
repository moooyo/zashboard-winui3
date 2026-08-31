using Zashboard.Core.Clash;
using Zashboard.Core.Normalization;

namespace Zashboard.Core.Tests;

[TestClass]
public sealed class ClashTypeNormalizerTests
{
    [TestMethod]
    [DataRow("URL-Test", ClashProxyKind.UrlTest)]
    [DataRow("reject-drop", ClashProxyKind.RejectDrop)]
    [DataRow("Load Balance", ClashProxyKind.LoadBalance)]
    [DataRow("PASS-RULE", ClashProxyKind.PassRule)]
    [DataRow("unknown-proxy", ClashProxyKind.Unknown)]
    public void ProxyKindsIgnoreFormatting(string value, ClashProxyKind expected)
    {
        Assert.AreEqual(expected, ClashTypeNormalizer.NormalizeProxyKind(value));
    }

    [TestMethod]
    [DataRow("warn", ClashLogLevel.Warning)]
    [DataRow("WARNING", ClashLogLevel.Warning)]
    [DataRow("trace", ClashLogLevel.Trace)]
    [DataRow("silent", ClashLogLevel.Silent)]
    [DataRow("verbose", ClashLogLevel.Unknown)]
    public void LogLevelsNormalizeKnownAliases(string value, ClashLogLevel expected)
    {
        Assert.AreEqual(expected, ClashTypeNormalizer.NormalizeLogLevel(value));
    }

    [TestMethod]
    [DataRow("honk 0.4.0", ClashCoreKind.Honk)]
    [DataRow("daemon-honk-release", ClashCoreKind.Honk)]
    [DataRow("honkey proxy", ClashCoreKind.Mihomo)]
    [DataRow("honk_core", ClashCoreKind.Mihomo)]
    [DataRow("Mihomo Meta", ClashCoreKind.Mihomo)]
    [DataRow("", ClashCoreKind.Unknown)]
    public void CoreClassificationUsesHonkWordBoundaries(
        string value,
        ClashCoreKind expected)
    {
        Assert.AreEqual(expected, ClashTypeNormalizer.ClassifyCore(value));
    }

    [TestMethod]
    public void UnixTimestampAcceptsSecondsAndMilliseconds()
    {
        Assert.IsTrue(ClashTypeNormalizer.TryNormalizeUnixTimestamp(
            1_700_000_000,
            out DateTimeOffset seconds));
        Assert.IsTrue(ClashTypeNormalizer.TryNormalizeUnixTimestamp(
            1_700_000_000_000,
            out DateTimeOffset milliseconds));

        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), seconds);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000), milliseconds);
        Assert.IsFalse(ClashTypeNormalizer.TryNormalizeUnixTimestamp(long.MaxValue, out _));
    }

    [TestMethod]
    [DataRow("udp", "443", "", ClashNetworkKind.Quic)]
    [DataRow("udp", "53", "dns.example", ClashNetworkKind.Quic)]
    [DataRow("udp", "53", "", ClashNetworkKind.Udp)]
    [DataRow("tcp", "443", "host.example", ClashNetworkKind.Tcp)]
    public void NetworkClassificationRecognizesQuicHeuristic(
        string network,
        string port,
        string sniffHost,
        ClashNetworkKind expected)
    {
        Assert.AreEqual(
            expected,
            ClashTypeNormalizer.NormalizeNetwork(network, port, sniffHost));
    }
}
