using System.Collections.Specialized;
using Zashboard.App.Controls;
using Zashboard.App.Services;
using Zashboard.App.ViewModels;
using Zashboard.Core.Backends;
using Zashboard.Core.Clash;
using Zashboard.Core.Sessions;
using Zashboard.Infrastructure.Persistence;

namespace Zashboard.App.Logic.Tests;

[TestClass]
public sealed class ProxiesViewModelTests
{
    [TestMethod]
    public async Task NonSmartAutomaticGroupMarksCurrentNodeWithoutSmartMetrics()
    {
        ProxyCatalog catalog = CreateCatalog(new ClashProxy
        {
            Name = "Automatic",
            Type = "URLTest",
            Kind = ClashProxyKind.UrlTest,
            All = ["Node A", "Node B"],
            Now = "Node B",
            Selectable = false,
        });
        await using AppSessionCoordinator coordinator = await CreateCoordinatorAsync(catalog);
        using ProxiesViewModel viewModel = new(coordinator, new AppSettingsState(null));

        ProxyGroupCardDisplayItem card = viewModel.ProxyGroupCards.Single();
        ProxyNodeDisplayItem nodeA = card.Nodes.Single(node => node.Name == "Node A");
        ProxyNodeDisplayItem nodeB = card.Nodes.Single(node => node.Name == "Node B");

        Assert.IsFalse(nodeA.IsCurrent);
        Assert.IsTrue(nodeB.IsCurrent);
        Assert.IsFalse(nodeB.CanSelect);
        Assert.IsFalse(nodeB.SupportsManualSelection);
        Assert.IsTrue(nodeB.IsSelectionUnavailable);
        Assert.IsFalse(nodeB.ShowSmartMetrics);
        Assert.AreEqual("--", nodeB.Rank);
        Assert.AreEqual("--", nodeB.Weight);
        Assert.AreEqual(
            "Active automatically; manual selection unavailable",
            nodeB.SelectionStatus);
        Assert.IsFalse(nodeB.AccessibleDescription.Contains("Rank:", StringComparison.Ordinal));
        Assert.HasCount(2, card.Nodes);
        Assert.AreEqual("Automatic", card.Nodes[0].GroupName);
    }

    [TestMethod]
    public async Task SmartGroupUsesFixedNodeAndPublishesRankAndSelectionState()
    {
        ProxyCatalog catalog = CreateCatalog(new ClashProxy
        {
            Name = "Smart",
            Type = "Smart",
            Kind = ClashProxyKind.Smart,
            All = ["Node A", "Node B"],
            Now = "Node A",
            Fixed = "Node B",
        });
        SmartWeights weights = new()
        {
            Weights = new Dictionary<string, IReadOnlyList<SmartNodeRank>>(StringComparer.Ordinal)
            {
                ["Smart"] =
                [
                    new SmartNodeRank { Name = "Node A", Rank = "1", Weight = 0.75 },
                    new SmartNodeRank { Name = "Node B", Rank = "2", Weight = 0.25 },
                ],
            },
        };
        await using AppSessionCoordinator coordinator = await CreateCoordinatorAsync(catalog, weights);
        using ProxiesViewModel viewModel = new(coordinator, new AppSettingsState(null));

        ProxyGroupCardDisplayItem card = viewModel.ProxyGroupCards.Single();
        ProxyNodeDisplayItem nodeA = card.Nodes.Single(node => node.Name == "Node A");
        ProxyNodeDisplayItem nodeB = card.Nodes.Single(node => node.Name == "Node B");

        Assert.IsFalse(nodeA.IsCurrent);
        Assert.IsTrue(nodeB.IsCurrent);
        Assert.IsTrue(nodeB.CanSelect);
        Assert.IsTrue(nodeB.SupportsManualSelection);
        Assert.IsTrue(nodeB.ShowSmartMetrics);
        Assert.AreEqual("2", nodeB.Rank);
        Assert.AreNotEqual("--", nodeB.Weight);
        Assert.AreEqual("Current selection", nodeB.SelectionStatus);
        StringAssert.Contains(nodeB.AccessibleDescription, "Rank: 2");
        StringAssert.Contains(nodeB.AccessibleDescription, "Current selection");
        Assert.IsTrue(card.IsSmart);
        Assert.IsTrue(card.CanRefreshSmartWeights);
        Assert.AreEqual("Node B", card.SelectedProxy);
    }

    [TestMethod]
    public void ProviderAccessibilityNamesIncludeRowContext()
    {
        ProxyProviderDisplayItem provider = new(
            "Provider A",
            "HTTP",
            3,
            "1.0 GiB / 10.0 GiB",
            "12/31/2026 11:59 PM",
            "8/31/2026 2:00 PM",
            true,
            true);

        Assert.AreEqual("Update provider Provider A", provider.UpdateActionName);
        Assert.AreEqual("Run health check for provider Provider A", provider.CheckActionName);
        StringAssert.Contains(provider.AccessibleDescription, "3 nodes");
        StringAssert.Contains(provider.AccessibleDescription, "1.0 GiB / 10.0 GiB");
    }

    [TestMethod]
    public void GroupDisplayFallsBackToCurrentNodeWhenFixedValueIsBlank()
    {
        ProxyGroupDisplayItem group = DisplayModelFactory.ProxyGroup(new ClashProxy
        {
            Name = "Smart",
            Type = "Smart",
            Fixed = " ",
            Now = "Node A",
            All = ["Node A"],
        });

        Assert.AreEqual("Node A", group.SelectedProxy);
        Assert.IsFalse(group.HasFixedProxy);
        Assert.IsTrue(group.IsSmart);
    }

    [TestMethod]
    public async Task RefreshReconcilesCardsAndNodesWithoutResettingTheirCollections()
    {
        ProxyCatalog initialCatalog = CreateCatalog(
            new ClashProxy
            {
                Name = "Selector",
                Type = "Selector",
                Kind = ClashProxyKind.Selector,
                All = ["Node A", "Node B"],
                Now = "Node A",
            },
            nodeADelay: 10,
            nodeBDelay: 20);
        ProxyTestHarness harness = await CreateHarnessAsync(initialCatalog);
        await using AppSessionCoordinator coordinator = harness.Coordinator;
        using ProxiesViewModel viewModel = new(coordinator, new AppSettingsState(null));
        ProxyGroupCardDisplayItem card = viewModel.ProxyGroupCards.Single();
        ProxyNodeDisplayItem nodeA = card.Nodes.Single(node => node.Name == "Node A");
        ProxyNodeDisplayItem nodeB = card.Nodes.Single(node => node.Name == "Node B");
        List<NotifyCollectionChangedAction> cardActions = [];
        List<NotifyCollectionChangedAction> nodeActions = [];
        List<string?> cardProperties = [];
        List<string?> nodeProperties = [];
        viewModel.ProxyGroupCards.CollectionChanged += (_, args) => cardActions.Add(args.Action);
        card.Nodes.CollectionChanged += (_, args) => nodeActions.Add(args.Action);
        card.PropertyChanged += (_, args) => cardProperties.Add(args.PropertyName);
        nodeA.PropertyChanged += (_, args) => nodeProperties.Add(args.PropertyName);

        harness.Session.Rest.ProxiesResult = CreateCatalog(
            new ClashProxy
            {
                Name = "Selector",
                Type = "Selector",
                Kind = ClashProxyKind.Selector,
                All = ["Node A", "Node B"],
                Now = "Node B",
            },
            nodeADelay: 30,
            nodeBDelay: 40);
        await viewModel.RefreshAsync();

        ProxyGroupCardDisplayItem updatedCard = viewModel.ProxyGroupCards.Single();
        Assert.AreSame(card, updatedCard);
        Assert.AreSame(nodeA, updatedCard.Nodes.Single(node => node.Name == "Node A"));
        Assert.AreSame(nodeB, updatedCard.Nodes.Single(node => node.Name == "Node B"));
        Assert.AreEqual("Node B", updatedCard.SelectedProxy);
        Assert.IsFalse(nodeA.IsCurrent);
        Assert.IsTrue(nodeB.IsCurrent);
        Assert.AreEqual("30 ms", nodeA.Latency);
        Assert.AreEqual("40 ms", nodeB.Latency);
        CollectionAssert.DoesNotContain(cardActions, NotifyCollectionChangedAction.Reset);
        CollectionAssert.DoesNotContain(nodeActions, NotifyCollectionChangedAction.Reset);
        CollectionAssert.Contains(cardProperties, nameof(ProxyGroupCardDisplayItem.SelectedProxy));
        CollectionAssert.Contains(nodeProperties, nameof(ProxyNodeDisplayItem.IsCurrent));
        CollectionAssert.Contains(nodeProperties, nameof(ProxyNodeDisplayItem.Latency));
        CollectionAssert.Contains(nodeProperties, nameof(ProxyNodeDisplayItem.AccessibleDescription));
    }

    [TestMethod]
    public async Task DelayTargetChangePreservesVisibleGroupCardsAndNodes()
    {
        ProxyCatalog catalog = CreateCatalog(new ClashProxy
        {
            Name = "Automatic",
            Type = "URLTest",
            Kind = ClashProxyKind.UrlTest,
            All = ["Node A", "Node B"],
            Now = "Node A",
        });
        await using AppSessionCoordinator coordinator = await CreateCoordinatorAsync(catalog);
        AppSettingsState settings = new(null);
        using ProxiesViewModel viewModel = new(coordinator, settings);
        ProxyGroupCardDisplayItem initial = viewModel.ProxyGroupCards.Single();
        ProxyNodeDisplayItem initialNode = initial.Nodes[0];

        settings.DelayTestUrl = "https://example.com/generate_204";

        Assert.AreSame(initial, viewModel.ProxyGroupCards.Single());
        Assert.AreSame(initialNode, viewModel.ProxyGroupCards.Single().Nodes[0]);
    }

    private static ProxyCatalog CreateCatalog(
        ClashProxy group,
        int nodeADelay = 0,
        int nodeBDelay = 0) => new()
    {
        Proxies = new Dictionary<string, ClashProxy>(StringComparer.Ordinal)
        {
            [group.Name] = group,
            ["Node A"] = new ClashProxy
            {
                Name = "Node A",
                Type = "Shadowsocks",
                Kind = ClashProxyKind.Unknown,
                Alive = true,
                History = CreateHistory(nodeADelay),
            },
            ["Node B"] = new ClashProxy
            {
                Name = "Node B",
                Type = "WireGuard",
                Kind = ClashProxyKind.Unknown,
                Alive = true,
                History = CreateHistory(nodeBDelay),
            },
        },
    };

    private static IReadOnlyList<ProxyHistoryEntry> CreateHistory(int delay) => delay > 0
        ? [new ProxyHistoryEntry { Time = "2026-08-31T00:00:00Z", Delay = delay }]
        : [];

    private static async Task<AppSessionCoordinator> CreateCoordinatorAsync(
        ProxyCatalog catalog,
        SmartWeights? smartWeights = null)
    {
        ProxyTestHarness harness = await CreateHarnessAsync(catalog, smartWeights);
        return harness.Coordinator;
    }

    private static async Task<ProxyTestHarness> CreateHarnessAsync(
        ProxyCatalog catalog,
        SmartWeights? smartWeights = null)
    {
        BackendProfile profile = new(
            Guid.NewGuid(),
            "Backend",
            BackendEndpoint.Create("http://127.0.0.1:9090"));
        FakeBackendProfileStore profiles = new()
        {
            Current = new BackendProfileSet
            {
                Profiles = [profile],
                ActiveProfileId = profile.Id,
            },
        };
        FakeBackendSession? activeSession = null;
        FakeBackendSessionFactory sessions = new()
        {
            CreateHandler = (createdProfile, _, epoch, _) =>
            {
                FakeBackendSession session = new(epoch, createdProfile);
                session.Rest.ProxiesResult = catalog;
                session.Rest.SmartWeightsResult = smartWeights ?? new SmartWeights();
                activeSession = session;
                return ValueTask.FromResult<IBackendSession>(session);
            },
        };
        AppSessionCoordinator coordinator = new(
            profiles,
            new FakeBackendCredentialStore(),
            sessions,
            new InlineTestDispatcher(),
            new AppSettingsState(null),
            TimeProvider.System);
        await coordinator.RunUserOperationAsync(coordinator.InitializeAsync);
        return new ProxyTestHarness(
            coordinator,
            activeSession ?? throw new InvalidOperationException("The test session was not created."));
    }

    private sealed record ProxyTestHarness(
        AppSessionCoordinator Coordinator,
        FakeBackendSession Session);
}
