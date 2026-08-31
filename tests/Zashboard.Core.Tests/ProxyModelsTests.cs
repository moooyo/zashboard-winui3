using Zashboard.Core.Clash;

namespace Zashboard.Core.Tests;

[TestClass]
public sealed class ProxyModelsTests
{
    [TestMethod]
    public void SelectorAndSmartGroupsAreSelectableByDefault()
    {
        Assert.IsTrue(new ClashProxy { Kind = ClashProxyKind.Selector }.AllowsManualSelection);
        Assert.IsTrue(new ClashProxy { Kind = ClashProxyKind.Smart }.AllowsManualSelection);
    }

    [TestMethod]
    public void AutomaticGroupsAreNotSelectableByDefault()
    {
        ClashProxyKind[] kinds =
        [
            ClashProxyKind.UrlTest,
            ClashProxyKind.Fallback,
            ClashProxyKind.LoadBalance,
        ];

        foreach (ClashProxyKind kind in kinds)
        {
            Assert.IsFalse(new ClashProxy { Kind = kind }.AllowsManualSelection);
        }
    }

    [TestMethod]
    public void ExplicitSelectableFlagOverridesTheDefault()
    {
        Assert.IsTrue(new ClashProxy
        {
            Kind = ClashProxyKind.Unknown,
            Selectable = true,
        }.AllowsManualSelection);
        Assert.IsFalse(new ClashProxy
        {
            Kind = ClashProxyKind.Selector,
            Selectable = false,
        }.AllowsManualSelection);
    }
}
