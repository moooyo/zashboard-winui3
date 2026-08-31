using Zashboard.App.Controls;
using Zashboard.App.Services;
using Zashboard.App.ViewModels;
using Zashboard.Core.Clash;

namespace Zashboard.App.Logic.Tests;

[TestClass]
public sealed class RulesViewModelTests
{
    [TestMethod]
    public async Task QueryPipelineFiltersCatalogAndPublishesFilteredEmptyState()
    {
        await using AppSessionCoordinator coordinator = new(
            new FakeBackendProfileStore(),
            new FakeBackendCredentialStore(),
            new FakeBackendSessionFactory(),
            new InlineTestDispatcher(),
            new AppSettingsState(null),
            TimeProvider.System);
        using RulesViewModel viewModel = new(coordinator);
        bool notifiedFilteredEmpty = false;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(RulesViewModel.IsFilteredEmpty) &&
                viewModel.IsFilteredEmpty)
            {
                notifiedFilteredEmpty = true;
            }
        };
        coordinator.SetRuleCatalogForTest(new RuleCatalog
        {
            Rules =
            [
                new ClashRule
                {
                    Type = "SCRIPT",
                    Payload = "script-rule",
                    Proxy = "REJECT",
                    Index = 10,
                },
                new ClashRule
                {
                    Type = "IP-CIDR",
                    Payload = "10.0.0.0/8",
                    Proxy = "DIRECT",
                    Index = 42,
                },
                new ClashRule
                {
                    Type = "DOMAIN-SUFFIX",
                    Payload = "example.com",
                    Proxy = "Proxy",
                    Index = 75,
                },
            ],
        });

        await viewModel.SetQueryAsync(null, "network");

        Assert.AreEqual(3, viewModel.TotalRuleCount);
        Assert.HasCount(1, viewModel.Rules);
        Assert.AreEqual("IP-CIDR", viewModel.Rules[0].Type);
        Assert.AreEqual(42, viewModel.Rules[0].ApiIndex);
        Assert.IsFalse(viewModel.IsFilteredEmpty);

        await viewModel.SetQueryAsync("not-present", "network");

        Assert.IsEmpty(viewModel.Rules);
        Assert.IsTrue(viewModel.IsFilteredEmpty);
        Assert.IsTrue(notifiedFilteredEmpty);
    }

    [TestMethod]
    [DataRow("IP-CIDR", true)]
    [DataRow("IP-CIDR6", true)]
    [DataRow("SRC-IP-CIDR", true)]
    [DataRow("GEOIP", true)]
    [DataRow("NETWORK", true)]
    [DataRow("SCRIPT", false)]
    [DataRow("DOMAIN-SUFFIX", false)]
    public void NetworkFilterMatchesOnlyNetworkTypeTokens(string type, bool expected)
    {
        Assert.AreEqual(expected, RulesViewModel.MatchesRuleTypeFilter(type, "network"));
    }

    [TestMethod]
    public void FilteredEmptyRequiresSourceRulesAndAnActiveFilter()
    {
        Assert.IsTrue(RulesViewModel.IsFilteredEmptyState(3, 0, "missing", "all"));
        Assert.IsTrue(RulesViewModel.IsFilteredEmptyState(3, 0, string.Empty, "network"));
        Assert.IsFalse(RulesViewModel.IsFilteredEmptyState(0, 0, "missing", "network"));
        Assert.IsFalse(RulesViewModel.IsFilteredEmptyState(3, 1, "missing", "network"));
        Assert.IsFalse(RulesViewModel.IsFilteredEmptyState(3, 0, string.Empty, "all"));
    }

    [TestMethod]
    public void RuleDisplayItemExposesPositiveEnabledStateAndAccessibleText()
    {
        RuleDisplayItem enabled = CreateRule(disabled: false);
        RuleDisplayItem disabled = CreateRule(disabled: true);

        Assert.IsTrue(enabled.Enabled);
        Assert.IsFalse(disabled.Enabled);
        Assert.AreEqual(
            "Disable rule 7, currently enabled",
            enabled.ToggleAccessibleName);
        Assert.AreEqual(
            "Enable rule 7, currently disabled",
            disabled.ToggleAccessibleName);
        StringAssert.Contains(disabled.AccessibleDescription, "State: Disabled.");
    }

    [TestMethod]
    public void ProviderDisplayItemNamesItsUpdateAction()
    {
        RuleProviderDisplayItem provider = new(
            "Regional routes",
            "HTTP",
            "Domain",
            "YAML",
            "25",
            "8/31/2026 10:00 AM",
            true);

        Assert.AreEqual("Update Regional routes rule provider", provider.UpdateAccessibleName);
        StringAssert.Contains(provider.AccessibleDescription, "Rules: 25.");
    }

    private static RuleDisplayItem CreateRule(bool disabled) => new(
        7,
        7,
        "DOMAIN-SUFFIX",
        "example.com",
        "DIRECT",
        "--",
        "rule-7",
        disabled,
        disabled ? "Disabled" : "Enabled",
        "0",
        "0",
        true);
}
