using Zashboard.Core.Clash;
using Zashboard.Core.Normalization;

namespace Zashboard.Core.Tests;

[TestClass]
public sealed class ConnectionNormalizerTests
{
    [TestMethod]
    public void GetHostPrefersHostThenSniffHostThenDestinationIp()
    {
        ClashConnection connection = CreateConnection(new ClashConnectionMetadata
        {
            Host = "service.example",
            SniffHost = "sniffed.example",
            DestinationIp = "192.0.2.10",
            DestinationPort = "443",
        });

        Assert.AreEqual("service.example:443", ConnectionNormalizer.GetHost(connection));

        connection = connection with
        {
            Metadata = connection.Metadata with { Host = string.Empty },
        };
        Assert.AreEqual("sniffed.example:443", ConnectionNormalizer.GetHost(connection));

        connection = connection with
        {
            Metadata = connection.Metadata with { SniffHost = string.Empty },
        };
        Assert.AreEqual("192.0.2.10:443", ConnectionNormalizer.GetHost(connection));
    }

    [TestMethod]
    public void GetHostBracketsIpv6WhenAppendingPort()
    {
        ClashConnection connection = CreateConnection(new ClashConnectionMetadata
        {
            DestinationIp = "2001:db8::20",
            DestinationPort = "8443",
        });

        Assert.AreEqual("[2001:db8::20]:8443", ConnectionNormalizer.GetHost(connection));
    }

    [TestMethod]
    [DataRow("", "C:\\Program Files\\Browser\\browser.exe", "browser.exe")]
    [DataRow("", "/usr/bin/curl", "curl")]
    [DataRow("reported-process", "C:\\ignored\\fallback.exe", "reported-process")]
    [DataRow("", "single-name", "single-name")]
    public void GetProcessNameUsesReportedNameOrPathFileName(
        string process,
        string processPath,
        string expected)
    {
        ClashConnection connection = CreateConnection(new ClashConnectionMetadata
        {
            Process = process,
            ProcessPath = processPath,
        });

        Assert.AreEqual(expected, ConnectionNormalizer.GetProcessName(connection));
    }

    [TestMethod]
    public void GetRuleDisplayNameIncludesPayloadOnlyWhenPresent()
    {
        ClashConnection connection = new()
        {
            Rule = "DOMAIN-SUFFIX",
            RulePayload = "example.com",
        };

        Assert.AreEqual(
            "DOMAIN-SUFFIX: example.com",
            ConnectionNormalizer.GetRuleDisplayName(connection));
        Assert.AreEqual(
            "MATCH",
            ConnectionNormalizer.GetRuleDisplayName(connection with
            {
                Rule = "MATCH",
                RulePayload = " ",
            }));
    }

    [TestMethod]
    public void CalculateTransferRateUsesCounterDeltaAndClampsCounterResets()
    {
        ClashConnection previous = new() { Id = "connection-1", Download = 1_000, Upload = 500 };
        ClashConnection current = new() { Id = "connection-1", Download = 1_250, Upload = 100 };

        ConnectionTransferRate rate = ConnectionNormalizer.CalculateTransferRate(current, previous);

        Assert.AreEqual(250, rate.Download);
        Assert.AreEqual(0, rate.Upload);
    }

    [TestMethod]
    public void CalculateTransferRateReturnsZeroWithoutMatchingPreviousConnection()
    {
        ClashConnection current = new() { Id = "connection-1", Download = 1_250, Upload = 100 };
        ClashConnection other = new() { Id = "connection-2", Download = 1_000, Upload = 50 };

        Assert.AreEqual(default, ConnectionNormalizer.CalculateTransferRate(current, null));
        Assert.AreEqual(default, ConnectionNormalizer.CalculateTransferRate(current, other));
    }

    [TestMethod]
    public void CalculateTransferRateNormalizesCounterDeltaToElapsedTime()
    {
        ClashConnection previous = new() { Id = "connection-1", Download = 1_000, Upload = 500 };
        ClashConnection current = new() { Id = "connection-1", Download = 1_500, Upload = 750 };

        ConnectionTransferRate rate = ConnectionNormalizer.CalculateTransferRate(
            current,
            previous,
            TimeSpan.FromMilliseconds(500));

        Assert.AreEqual(1_000, rate.Download);
        Assert.AreEqual(500, rate.Upload);
        Assert.AreEqual(
            default,
            ConnectionNormalizer.CalculateTransferRate(current, previous, TimeSpan.Zero));
    }

    private static ClashConnection CreateConnection(ClashConnectionMetadata metadata) => new()
    {
        Id = "connection-1",
        Metadata = metadata,
    };
}
