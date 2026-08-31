using System.Net;
using System.Text;
using System.Text.Json;
using Zashboard.Core.Backends;
using Zashboard.Core.Capabilities;
using Zashboard.Core.Clash;
using Zashboard.Infrastructure.Clash;

namespace Zashboard.Infrastructure.Tests;

[TestClass]
public sealed class ClashRestClientTests
{
    private static readonly string[] ExpectedProxyMembers = ["Node A", "Node B"];
    private static readonly string[] ExpectedModeList = ["rule", "global"];
    private static readonly string[] ExpectedModes = ["direct", "script"];

    private static readonly DateTimeOffset ObservedAt = new(
        2026,
        8,
        30,
        4,
        5,
        6,
        TimeSpan.Zero);

    [TestMethod]
    public async Task GetVersionSendsGetToBasePathWithBearerAndMapsJson()
    {
        using RecordingHttpMessageHandler handler = new(_ => JsonResponse(
            """
            {
              "VERSION": "Mihomo Meta v1.19.0",
              "futureField": true
            }
            """));
        using HttpClient httpClient = new(handler);
        ClashRestClient client = CreateClient(httpClient, new CapabilityRegistry());

        ClashVersion version = await client.GetVersionAsync();

        Assert.AreEqual("Mihomo Meta v1.19.0", version.Value);
        Assert.AreEqual(ClashCoreKind.Mihomo, version.CoreKind);
        CapturedHttpRequest request = handler.LastRequest;
        Assert.AreEqual(HttpMethod.Get, request.Method);
        Assert.AreEqual("https://controller.example/api/v1/version", request.Uri.AbsoluteUri);
        Assert.AreEqual("Bearer", request.Authorization?.Scheme);
        Assert.AreEqual("secret with spaces", request.Authorization?.Parameter);
        CollectionAssert.Contains(request.AcceptMediaTypes.ToArray(), "application/json");
        Assert.IsNull(request.Content);
    }

    [TestMethod]
    public async Task MeasureProxyDelayEncodesPathAndQueryValues()
    {
        using RecordingHttpMessageHandler handler = new(_ => JsonResponse("{\"delay\":42}"));
        using HttpClient httpClient = new(handler);
        ClashRestClient client = CreateClient(httpClient, new CapabilityRegistry());
        Uri target = new("https://probe.example/generate_204?region=East%20US&source=/api");

        ProxyDelay delay = await client.MeasureProxyDelayAsync(
            "Node / East",
            new ProxyDelayRequest(target, TimeSpan.FromMilliseconds(1500.1)));

        Assert.AreEqual(42, delay.Milliseconds);
        CapturedHttpRequest request = handler.LastRequest;
        Assert.AreEqual(
            "/api/v1/proxies/Node%20%2F%20East/delay",
            request.Uri.AbsolutePath);
        Dictionary<string, string> query = ParseQuery(request.Uri);
        Assert.AreEqual(target.AbsoluteUri, query["url"]);
        Assert.AreEqual("1501", query["timeout"]);
    }

    [TestMethod]
    public async Task MeasureProxyGroupDelayMapsNamedResultsAndUsesGroupRoute()
    {
        using RecordingHttpMessageHandler handler = new(_ => JsonResponse(
            "{\"Node A\":\"42\",\"Node B\":0}"));
        using HttpClient httpClient = new(handler);
        ClashRestClient client = CreateClient(httpClient, new CapabilityRegistry());
        Uri target = new("https://probe.example/generate_204");

        IReadOnlyDictionary<string, ProxyDelay> delays =
            await client.MeasureProxyGroupDelayAsync(
                "Auto / Main",
                new ProxyDelayRequest(target, TimeSpan.FromSeconds(5)));

        Assert.AreEqual(42, delays["Node A"].Milliseconds);
        Assert.IsFalse(delays["Node B"].IsReachable);
        Assert.AreEqual(
            "/api/v1/group/Auto%20%2F%20Main/delay",
            handler.LastRequest.Uri.AbsolutePath);
        Dictionary<string, string> query = ParseQuery(handler.LastRequest.Uri);
        Assert.AreEqual(target.AbsoluteUri, query["url"]);
        Assert.AreEqual("5000", query["timeout"]);
    }

    [TestMethod]
    public async Task SelectProxySendsEncodedPutAndJsonBody()
    {
        using RecordingHttpMessageHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using HttpClient httpClient = new(handler);
        ClashRestClient client = CreateClient(httpClient, new CapabilityRegistry());

        await client.SelectProxyAsync("Primary / Auto", "Node A");

        CapturedHttpRequest request = handler.LastRequest;
        Assert.AreEqual(HttpMethod.Put, request.Method);
        Assert.AreEqual(
            "https://controller.example/api/v1/proxies/Primary%20%2F%20Auto",
            request.Uri.AbsoluteUri);
        Assert.AreEqual("application/json; charset=utf-8", request.ContentType);
        using JsonDocument body = JsonDocument.Parse(request.Content ?? string.Empty);
        Assert.AreEqual("Node A", body.RootElement.GetProperty("name").GetString());
    }

    [TestMethod]
    [DataRow("delete", "DELETE", "/api/v1/connections/id%20%2F%201")]
    [DataRow("post", "POST", "/api/v1/cache/dns/flush")]
    [DataRow("patch", "PATCH", "/api/v1/configs")]
    public async Task MutationsUseExpectedHttpMethodsAndRoutes(
        string operation,
        string expectedMethod,
        string expectedPath)
    {
        using RecordingHttpMessageHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using HttpClient httpClient = new(handler);
        ClashRestClient client = CreateClient(httpClient, new CapabilityRegistry());

        switch (operation)
        {
            case "delete":
                await client.CloseConnectionAsync("id / 1");
                break;
            case "post":
                await client.FlushDnsCacheAsync();
                break;
            case "patch":
                await client.PatchConfigurationAsync(new ClashConfigurationPatch { Mode = "rule" });
                break;
            default:
                Assert.Fail($"Unknown test operation: {operation}");
                break;
        }

        CapturedHttpRequest request = handler.LastRequest;
        Assert.AreEqual(expectedMethod, request.Method.Method);
        Assert.AreEqual(expectedPath, request.Uri.AbsolutePath);
    }

    [TestMethod]
    public async Task MaintenanceAndCacheMutationsUseExpectedRoutesAndCapabilities()
    {
        CapabilityRegistry capabilities = new();
        using RecordingHttpMessageHandler handler = new(_ =>
            new HttpResponseMessage(HttpStatusCode.NoContent));
        using HttpClient httpClient = new(handler);
        ClashRestClient client = CreateClient(httpClient, capabilities);

        await client.ClearFixedProxyAsync("Fixed / Group");
        await client.CloseAllConnectionsAsync();
        await client.BlockSmartConnectionAsync("connection / 1");
        await client.FlushFakeIpCacheAsync();
        await client.UpdateGeoDataAsync();
        await client.UpgradeCoreAsync(CoreUpgradeChannel.Auto);
        await client.UpgradeCoreAsync(CoreUpgradeChannel.Release);
        await client.UpgradeCoreAsync(CoreUpgradeChannel.Alpha);
        await client.RestartCoreAsync();
        await client.UpgradeDashboardAsync();

        (HttpMethod Method, string Path)[] expected =
        [
            (HttpMethod.Delete, "/api/v1/proxies/Fixed%20%2F%20Group"),
            (HttpMethod.Delete, "/api/v1/connections"),
            (HttpMethod.Delete, "/api/v1/connections/smart/connection%20%2F%201"),
            (HttpMethod.Post, "/api/v1/cache/fakeip/flush"),
            (HttpMethod.Post, "/api/v1/configs/geo"),
            (HttpMethod.Post, "/api/v1/upgrade"),
            (HttpMethod.Post, "/api/v1/upgrade"),
            (HttpMethod.Post, "/api/v1/upgrade"),
            (HttpMethod.Post, "/api/v1/restart"),
            (HttpMethod.Post, "/api/v1/upgrade/ui"),
        ];
        Assert.HasCount(expected.Length, handler.Requests);
        for (int index = 0; index < expected.Length; index++)
        {
            Assert.AreEqual(expected[index].Method, handler.Requests[index].Method);
            Assert.AreEqual(expected[index].Path, handler.Requests[index].Uri.AbsolutePath);
        }

        Assert.AreEqual(string.Empty, handler.Requests[5].Uri.Query);
        Assert.AreEqual("release", ParseQuery(handler.Requests[6].Uri)["channel"]);
        Assert.AreEqual("alpha", ParseQuery(handler.Requests[7].Uri)["channel"]);
        ClashCapability[] observedCapabilities =
        [
            ClashCapability.SmartConnectionBlock,
            ClashCapability.GeoDataUpdate,
            ClashCapability.CoreUpgrade,
            ClashCapability.CoreRestart,
            ClashCapability.DashboardUpgrade,
        ];
        foreach (ClashCapability capability in observedCapabilities)
        {
            Assert.AreEqual(
                CapabilitySupport.Supported,
                capabilities.GetObservation(capability).Support);
        }
    }

    [TestMethod]
    [DataRow(200)]
    [DataRow(204)]
    public async Task MutationAcceptsOkAndNoContentAndObservesCapability(int statusCode)
    {
        CapabilityRegistry capabilities = new();
        using RecordingHttpMessageHandler handler = new(_ => new HttpResponseMessage((HttpStatusCode)statusCode));
        using HttpClient httpClient = new(handler);
        ClashRestClient client = CreateClient(httpClient, capabilities);

        await client.UpdateProxyProviderAsync("Provider A");

        CapabilityObservation observation = capabilities.GetObservation(
            ClashCapability.ProviderProxyUpdate);
        Assert.AreEqual(CapabilitySupport.Supported, observation.Support);
        Assert.AreEqual(CapabilityEvidenceKind.SuccessfulCall, observation.Evidence);
        Assert.AreEqual(ObservedAt, observation.ObservedAt);
    }

    [TestMethod]
    public async Task ProviderHealthCheckAcceptsNoContentResponse()
    {
        CapabilityRegistry capabilities = new();
        using RecordingHttpMessageHandler handler = new(_ =>
            new HttpResponseMessage(HttpStatusCode.NoContent));
        using HttpClient httpClient = new(handler);
        ClashRestClient client = CreateClient(httpClient, capabilities);

        await client.CheckProxyProviderAsync("Provider / A");

        CapturedHttpRequest request = handler.LastRequest;
        Assert.AreEqual(HttpMethod.Get, request.Method);
        Assert.AreEqual(
            "/api/v1/providers/proxies/Provider%20%2F%20A/healthcheck",
            request.Uri.AbsolutePath);
        Assert.AreEqual(
            CapabilitySupport.Supported,
            capabilities.GetObservation(ClashCapability.ProviderProxyHealthCheck).Support);
    }

    [TestMethod]
    public async Task GetProxiesMapsKnownFieldsAndObservesResponseCapability()
    {
        CapabilityRegistry capabilities = new();
        using RecordingHttpMessageHandler handler = new(_ => JsonResponse(
            """
            {
              "proxies": {
                "Auto": {
                  "name": "Auto",
                  "type": "URL-Test",
                  "history": [
                    { "time": "2026-08-30T04:00:00Z", "delay": "37" }
                  ],
                  "extra": {
                    "https://probe.example/": {
                      "alive": true,
                      "history": [{ "time": "2026-08-30T04:01:00Z", "delay": 41 }]
                    }
                  },
                  "all": ["Node A", "", "Node B"],
                  "alive": true,
                  "udp": true,
                  "now": "Node A",
                  "testUrl": "https://probe.example/",
                  "futureField": { "nested": true }
                }
              }
            }
            """));
        using HttpClient httpClient = new(handler);
        ClashRestClient client = CreateClient(httpClient, capabilities);

        ProxyCatalog catalog = await client.GetProxiesAsync();

        ClashProxy proxy = catalog.Proxies["Auto"];
        Assert.AreEqual(ClashProxyKind.UrlTest, proxy.Kind);
        Assert.AreEqual(37, proxy.History[0].Delay);
        CollectionAssert.AreEqual(ExpectedProxyMembers, proxy.All.ToArray());
        Assert.AreEqual("Node A", proxy.Now);
        Assert.AreEqual("https://probe.example/", proxy.TestUrl?.AbsoluteUri);
        Assert.IsTrue(proxy.Extra["https://probe.example/"].Alive);
        Assert.AreEqual(41, proxy.Extra["https://probe.example/"].History[0].Delay);
        CapabilityObservation observation = capabilities.GetObservation(
            ClashCapability.IndependentLatencyHistory);
        Assert.AreEqual(CapabilitySupport.Supported, observation.Support);
        Assert.AreEqual(CapabilityEvidenceKind.ResponseData, observation.Evidence);
        Assert.AreEqual(ObservedAt, observation.ObservedAt);
    }

    [TestMethod]
    public async Task GetConfigurationMapsWireFixtureAndUsesConfigsRoute()
    {
        using RecordingHttpMessageHandler handler = new(_ => JsonResponse(
            """
            {
              "port": 7890,
              "socks-port": 7891,
              "redir-port": 7892,
              "tproxy-port": 7893,
              "mixed-port": 7894,
              "allow-lan": true,
              "bind-address": "0.0.0.0",
              "mode": "rule",
              "mode-list": ["rule", "", "global"],
              "modes": ["direct", "script"],
              "log-level": "warning",
              "ipv6": true,
              "tun": { "enable": true },
              "futureField": 1
            }
            """));
        using HttpClient httpClient = new(handler);
        ClashRestClient client = CreateClient(httpClient, new CapabilityRegistry());

        ClashConfiguration configuration = await client.GetConfigurationAsync();

        Assert.AreEqual(7890, configuration.Port);
        Assert.AreEqual(7891, configuration.SocksPort);
        Assert.AreEqual(7892, configuration.RedirPort);
        Assert.AreEqual(7893, configuration.TProxyPort);
        Assert.AreEqual(7894, configuration.MixedPort);
        Assert.IsTrue(configuration.AllowLan);
        Assert.AreEqual("0.0.0.0", configuration.BindAddress);
        Assert.AreEqual("rule", configuration.Mode);
        CollectionAssert.AreEqual(ExpectedModeList, configuration.ModeList.ToArray());
        CollectionAssert.AreEqual(ExpectedModes, configuration.Modes.ToArray());
        Assert.AreEqual("warning", configuration.LogLevel);
        Assert.IsTrue(configuration.Ipv6);
        Assert.IsNotNull(configuration.Tun);
        Assert.IsTrue(configuration.Tun.Enabled);
        Assert.AreEqual(HttpMethod.Get, handler.LastRequest.Method);
        Assert.AreEqual("/api/v1/configs", handler.LastRequest.Uri.AbsolutePath);
    }

    [TestMethod]
    public async Task ConfigurationWithoutTunKeepsTunUnavailable()
    {
        using RecordingHttpMessageHandler handler = new(_ => JsonResponse(
            "{\"port\":7890,\"socks-port\":7891,\"redir-port\":0,\"tproxy-port\":0,\"mixed-port\":7892,\"allow-lan\":false,\"mode\":\"rule\"}"));
        using HttpClient httpClient = new(handler);
        ClashRestClient client = CreateClient(httpClient, new CapabilityRegistry());

        ClashConfiguration configuration = await client.GetConfigurationAsync();

        Assert.IsNull(configuration.Tun);
    }

    [TestMethod]
    public async Task ConfigurationMutationsSendExpectedQueriesAndBodies()
    {
        using RecordingHttpMessageHandler handler = new(_ =>
            new HttpResponseMessage(HttpStatusCode.NoContent));
        using HttpClient httpClient = new(handler);
        ClashRestClient client = CreateClient(httpClient, new CapabilityRegistry());

        await client.PatchConfigurationAsync(new ClashConfigurationPatch
        {
            Port = 7890,
            AllowLan = true,
            Mode = "global",
            Ipv6 = false,
            TunEnabled = true,
        });
        await client.ReloadConfigurationAsync();
        await client.UpdateConfigurationAsync(new ClashConfigurationUpdate
        {
            Path = "C:\\profiles\\active.yaml",
            Payload = "mode: rule",
            Force = true,
        });

        Assert.HasCount(3, handler.Requests);
        CapturedHttpRequest patch = handler.Requests[0];
        Assert.AreEqual(HttpMethod.Patch, patch.Method);
        Assert.AreEqual("/api/v1/configs", patch.Uri.AbsolutePath);
        using (JsonDocument body = JsonDocument.Parse(patch.Content ?? string.Empty))
        {
            Assert.AreEqual(7890, body.RootElement.GetProperty("port").GetInt32());
            Assert.IsTrue(body.RootElement.GetProperty("allow-lan").GetBoolean());
            Assert.AreEqual("global", body.RootElement.GetProperty("mode").GetString());
            Assert.IsFalse(body.RootElement.GetProperty("ipv6").GetBoolean());
            Assert.IsTrue(body.RootElement.GetProperty("tun").GetProperty("enable").GetBoolean());
            Assert.IsFalse(body.RootElement.TryGetProperty("socks-port", out _));
        }

        CapturedHttpRequest reload = handler.Requests[1];
        Assert.AreEqual(HttpMethod.Put, reload.Method);
        Assert.AreEqual("true", ParseQuery(reload.Uri)["reload"]);
        using (JsonDocument body = JsonDocument.Parse(reload.Content ?? string.Empty))
        {
            Assert.AreEqual(string.Empty, body.RootElement.GetProperty("path").GetString());
            Assert.AreEqual(string.Empty, body.RootElement.GetProperty("payload").GetString());
        }

        CapturedHttpRequest update = handler.Requests[2];
        Assert.AreEqual(HttpMethod.Put, update.Method);
        Assert.AreEqual("true", ParseQuery(update.Uri)["force"]);
        using JsonDocument updateBody = JsonDocument.Parse(update.Content ?? string.Empty);
        Assert.AreEqual(
            "C:\\profiles\\active.yaml",
            updateBody.RootElement.GetProperty("path").GetString());
        Assert.AreEqual("mode: rule", updateBody.RootElement.GetProperty("payload").GetString());
    }

    [TestMethod]
    public async Task GetRulesMapsStatisticsAndObservesRuleExtensionCapabilities()
    {
        CapabilityRegistry capabilities = new();
        using RecordingHttpMessageHandler handler = new(_ => JsonResponse(
            """
            {
              "rules": [
                {
                  "type": "DOMAIN-SUFFIX",
                  "payload": "example.com",
                  "proxy": "Proxy",
                  "size": 12,
                  "uuid": "rule-uuid",
                  "disabled": true,
                  "index": 7,
                  "extra": {
                    "disabled": true,
                    "hitAt": "2026-08-30T04:00:00Z",
                    "hitCount": 9,
                    "missAt": "2026-08-30T04:01:00Z",
                    "missCount": 3
                  },
                  "futureField": true
                }
              ]
            }
            """));
        using HttpClient httpClient = new(handler);
        ClashRestClient client = CreateClient(httpClient, capabilities);

        RuleCatalog catalog = await client.GetRulesAsync();

        ClashRule rule = catalog.Rules.Single();
        Assert.AreEqual("DOMAIN-SUFFIX", rule.Type);
        Assert.AreEqual("example.com", rule.Payload);
        Assert.AreEqual("Proxy", rule.Proxy);
        Assert.AreEqual(12L, rule.Size);
        Assert.AreEqual("rule-uuid", rule.Identifier);
        Assert.IsTrue(rule.Disabled);
        Assert.AreEqual(7, rule.Index);
        Assert.IsNotNull(rule.Statistics);
        Assert.AreEqual(9L, rule.Statistics.HitCount);
        Assert.AreEqual(
            new DateTimeOffset(2026, 8, 30, 4, 0, 0, TimeSpan.Zero),
            rule.Statistics.HitAt);
        Assert.AreEqual(3L, rule.Statistics.MissCount);
        Assert.AreEqual(
            CapabilitySupport.Supported,
            capabilities.GetObservation(ClashCapability.RuleDisableByIndex).Support);
        Assert.AreEqual(
            CapabilitySupport.Supported,
            capabilities.GetObservation(ClashCapability.RuleDisableByIdentifier).Support);
        Assert.AreEqual("/api/v1/rules", handler.LastRequest.Uri.AbsolutePath);
    }

    [TestMethod]
    public async Task RuleMutationsSendExpectedMethodsPathsAndBody()
    {
        using RecordingHttpMessageHandler handler = new(_ =>
            new HttpResponseMessage(HttpStatusCode.NoContent));
        using HttpClient httpClient = new(handler);
        ClashRestClient client = CreateClient(httpClient, new CapabilityRegistry());

        await client.SetRuleDisabledStatesAsync(new Dictionary<int, bool>
        {
            [7] = true,
            [2] = false,
        });
        await client.ToggleRuleDisabledByIdentifierAsync("uuid / one");
        await client.UpdateRuleProviderAsync("Rules / Main");

        CapturedHttpRequest disable = handler.Requests[0];
        Assert.AreEqual(HttpMethod.Patch, disable.Method);
        Assert.AreEqual("/api/v1/rules/disable", disable.Uri.AbsolutePath);
        using (JsonDocument body = JsonDocument.Parse(disable.Content ?? string.Empty))
        {
            Assert.IsTrue(body.RootElement.GetProperty("7").GetBoolean());
            Assert.IsFalse(body.RootElement.GetProperty("2").GetBoolean());
        }

        Assert.AreEqual(HttpMethod.Put, handler.Requests[1].Method);
        Assert.AreEqual("/api/v1/rules/uuid%20%2F%20one", handler.Requests[1].Uri.AbsolutePath);
        Assert.AreEqual(HttpMethod.Put, handler.Requests[2].Method);
        Assert.AreEqual(
            "/api/v1/providers/rules/Rules%20%2F%20Main",
            handler.Requests[2].Uri.AbsolutePath);
    }

    [TestMethod]
    public async Task ProviderCatalogsMapRepresentativeWireFixtures()
    {
        using RecordingHttpMessageHandler handler = new(request =>
            request.Uri.AbsolutePath.EndsWith("/providers/proxies", StringComparison.Ordinal)
                ? JsonResponse(
                    """
                    {
                      "providers": {
                        "Provider A": {
                          "name": "",
                          "proxies": [
                            { "name": "Node A", "type": "Shadowsocks", "alive": true }
                          ],
                          "testUrl": "https://probe.example/generate_204",
                          "updatedAt": "2026-08-30T03:00:00Z",
                          "vehicleType": "HTTP",
                          "subscriptionInfo": {
                            "Download": 11,
                            "Upload": 12,
                            "Total": 1000,
                            "Expire": 2000
                          },
                          "futureField": "ignored"
                        }
                      }
                    }
                    """)
                : JsonResponse(
                    """
                    {
                      "providers": {
                        "Rule Set": {
                          "behavior": "domain",
                          "format": "mrs",
                          "name": "Remote Rules",
                          "ruleCount": 42,
                          "type": "Rule",
                          "updatedAt": "2026-08-30T03:30:00Z",
                          "vehicleType": "HTTP"
                        }
                      }
                    }
                    """));
        using HttpClient httpClient = new(handler);
        ClashRestClient client = CreateClient(httpClient, new CapabilityRegistry());

        ProxyProviderCatalog proxyCatalog = await client.GetProxyProvidersAsync();
        RuleProviderCatalog ruleCatalog = await client.GetRuleProvidersAsync();

        ClashProxyProvider proxyProvider = proxyCatalog.Providers["Provider A"];
        Assert.AreEqual("Provider A", proxyProvider.Name);
        Assert.AreEqual("Node A", proxyProvider.Proxies.Single().Name);
        Assert.IsTrue(proxyProvider.Proxies.Single().Alive.GetValueOrDefault());
        Assert.AreEqual("https://probe.example/generate_204", proxyProvider.TestUrl?.AbsoluteUri);
        Assert.AreEqual("HTTP", proxyProvider.VehicleType);
        Assert.IsNotNull(proxyProvider.Subscription);
        Assert.AreEqual(11L, proxyProvider.Subscription.Download);
        Assert.AreEqual(1000L, proxyProvider.Subscription.Total);

        ClashRuleProvider ruleProvider = ruleCatalog.Providers["Rule Set"];
        Assert.AreEqual("Remote Rules", ruleProvider.Name);
        Assert.AreEqual("domain", ruleProvider.Behavior);
        Assert.AreEqual("mrs", ruleProvider.Format);
        Assert.AreEqual(42L, ruleProvider.RuleCount);
        Assert.AreEqual("HTTP", ruleProvider.VehicleType);
        Assert.HasCount(2, handler.Requests);
        Assert.AreEqual("/api/v1/providers/proxies", handler.Requests[0].Uri.AbsolutePath);
        Assert.AreEqual("/api/v1/providers/rules", handler.Requests[1].Uri.AbsolutePath);
        Assert.IsTrue(handler.Requests.All(static request => request.Method == HttpMethod.Get));
    }

    [TestMethod]
    public async Task ProviderProxyDelayEncodesBothResourcesAndQuery()
    {
        using RecordingHttpMessageHandler handler = new(_ => JsonResponse("{\"delay\":81}"));
        using HttpClient httpClient = new(handler);
        ClashRestClient client = CreateClient(httpClient, new CapabilityRegistry());
        Uri target = new("https://probe.example/check?region=North%20America");

        ProxyDelay delay = await client.MeasureProviderProxyDelayAsync(
            "Provider / A",
            "Node ? One",
            new ProxyDelayRequest(target, TimeSpan.FromMilliseconds(500.2)));

        Assert.AreEqual(81, delay.Milliseconds);
        CapturedHttpRequest request = handler.LastRequest;
        Assert.AreEqual(HttpMethod.Get, request.Method);
        Assert.AreEqual(
            "/api/v1/providers/proxies/Provider%20%2F%20A/Node%20%3F%20One/healthcheck",
            request.Uri.AbsolutePath);
        Assert.AreEqual(target.AbsoluteUri, ParseQuery(request.Uri)["url"]);
        Assert.AreEqual("501", ParseQuery(request.Uri)["timeout"]);
    }

    [TestMethod]
    public async Task QueryDnsMapsWireFixtureAndEncodesNormalizedQuery()
    {
        using RecordingHttpMessageHandler handler = new(_ => JsonResponse(
            """
            {
              "AD": true,
              "CD": false,
              "RA": true,
              "RD": true,
              "TC": false,
              "status": 0,
              "Question": [
                { "Name": "example.com.", "Qtype": 28, "Qclass": 1 }
              ],
              "Answer": [
                { "TTL": 60, "data": "2001:db8::1", "name": "example.com.", "type": 28 }
              ]
            }
            """));
        using HttpClient httpClient = new(handler);
        ClashRestClient client = CreateClient(httpClient, new CapabilityRegistry());

        DnsQueryResult result = await client.QueryDnsAsync(
            new DnsQueryRequest(" example.com ", "aaaa"));

        Assert.IsTrue(result.AuthenticatedData);
        Assert.IsFalse(result.CheckingDisabled);
        Assert.IsTrue(result.RecursionAvailable);
        Assert.AreEqual(0, result.Status);
        Assert.AreEqual("example.com.", result.Questions.Single().Name);
        Assert.AreEqual(28, result.Questions.Single().Type);
        Assert.AreEqual("2001:db8::1", result.Answers.Single().Data);
        Assert.AreEqual(60, result.Answers.Single().Ttl);
        CapturedHttpRequest request = handler.LastRequest;
        Assert.AreEqual(HttpMethod.Get, request.Method);
        Assert.AreEqual("/api/v1/dns/query", request.Uri.AbsolutePath);
        Assert.AreEqual("example.com", ParseQuery(request.Uri)["name"]);
        Assert.AreEqual("AAAA", ParseQuery(request.Uri)["type"]);
    }

    [TestMethod]
    public async Task DashboardStorageMapsValuesAndMutationsUseExpectedWireShape()
    {
        CapabilityRegistry capabilities = new();
        using RecordingHttpMessageHandler handler = new(request =>
            request.Method == HttpMethod.Get
                ? JsonResponse(
                    """
                    {
                      "theme": "dark",
                      "refresh": 15,
                      "enabled": true,
                      "optional": null
                    }
                    """)
                : new HttpResponseMessage(HttpStatusCode.NoContent));
        using HttpClient httpClient = new(handler);
        ClashRestClient client = CreateClient(httpClient, capabilities);

        DashboardStorage loaded = await client.GetDashboardStorageAsync();
        await client.SetDashboardStorageAsync(new DashboardStorage
        {
            Values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["theme"] = "light",
                ["layout"] = "compact",
            },
        });
        await client.DeleteDashboardStorageAsync();

        Assert.AreEqual("dark", loaded.Values["theme"]);
        Assert.AreEqual("15", loaded.Values["refresh"]);
        Assert.AreEqual("true", loaded.Values["enabled"]);
        Assert.AreEqual(string.Empty, loaded.Values["optional"]);
        Assert.AreEqual(HttpMethod.Get, handler.Requests[0].Method);
        Assert.AreEqual(HttpMethod.Put, handler.Requests[1].Method);
        Assert.AreEqual(HttpMethod.Delete, handler.Requests[2].Method);
        Assert.IsTrue(handler.Requests.All(static request =>
            request.Uri.AbsolutePath == "/api/v1/storage/zashboard"));
        using JsonDocument body = JsonDocument.Parse(handler.Requests[1].Content ?? string.Empty);
        Assert.AreEqual("light", body.RootElement.GetProperty("theme").GetString());
        Assert.AreEqual("compact", body.RootElement.GetProperty("layout").GetString());
        Assert.AreEqual(
            CapabilitySupport.Supported,
            capabilities.GetObservation(ClashCapability.SettingsStorage).Support);
    }

    [TestMethod]
    public async Task SmartWeightsMapWireFixtureAndFlushUsesSmartCacheRoute()
    {
        CapabilityRegistry capabilities = new();
        using RecordingHttpMessageHandler handler = new(request =>
            request.Method == HttpMethod.Get
                ? JsonResponse(
                    """
                    {
                      "message": "ready",
                      "weights": {
                        "Auto": [
                          { "Name": "Node A", "Rank": "1", "Weight": 0.75 },
                          { "Name": "Node B", "Rank": "2", "Weight": 0.25 }
                        ]
                      }
                    }
                    """)
                : new HttpResponseMessage(HttpStatusCode.NoContent));
        using HttpClient httpClient = new(handler);
        ClashRestClient client = CreateClient(httpClient, capabilities);

        SmartWeights weights = await client.GetSmartWeightsAsync();
        await client.FlushSmartWeightsAsync();

        Assert.AreEqual("ready", weights.Message);
        Assert.HasCount(2, weights.Weights["Auto"]);
        Assert.AreEqual("Node A", weights.Weights["Auto"][0].Name);
        Assert.AreEqual("1", weights.Weights["Auto"][0].Rank);
        Assert.AreEqual(0.75d, weights.Weights["Auto"][0].Weight);
        Assert.AreEqual(HttpMethod.Get, handler.Requests[0].Method);
        Assert.AreEqual("/api/v1/group/weights", handler.Requests[0].Uri.AbsolutePath);
        Assert.AreEqual(HttpMethod.Post, handler.Requests[1].Method);
        Assert.AreEqual("/api/v1/cache/smart/flush", handler.Requests[1].Uri.AbsolutePath);
        Assert.AreEqual(
            CapabilitySupport.Supported,
            capabilities.GetObservation(ClashCapability.SmartWeights).Support);
        Assert.AreEqual(
            CapabilitySupport.Supported,
            capabilities.GetObservation(ClashCapability.SmartWeightReset).Support);
    }

    [TestMethod]
    [DataRow("malformed")]
    [DataRow("no-content")]
    [DataRow("empty")]
    [DataRow("null")]
    [DataRow("missing-required")]
    public async Task InvalidOptionalJsonDoesNotMarkCapabilitySupported(string responseKind)
    {
        CapabilityRegistry capabilities = new();
        using RecordingHttpMessageHandler handler = new(_ => responseKind switch
        {
            "malformed" => JsonResponse("{"),
            "no-content" => new HttpResponseMessage(HttpStatusCode.NoContent),
            "empty" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Empty, Encoding.UTF8, "application/json"),
            },
            "null" => JsonResponse("null"),
            "missing-required" => JsonResponse("{}"),
            _ => throw new InvalidOperationException($"Unknown response kind: {responseKind}"),
        });
        using HttpClient httpClient = new(handler);
        ClashRestClient client = CreateClient(httpClient, capabilities);

        _ = await Assert.ThrowsExactlyAsync<ClashProtocolException>(
            () => client.GetSmartWeightsAsync());

        Assert.AreEqual(
            CapabilityObservation.Unknown,
            capabilities.GetObservation(ClashCapability.SmartWeights));
    }

    [TestMethod]
    [DataRow("version", "{\"version\":null}")]
    [DataRow("version", "{\"version\":\"   \"}")]
    [DataRow("proxies", "{\"proxies\":null}")]
    [DataRow("proxy-providers", "{\"providers\":null}")]
    [DataRow("rules", "{\"rules\":null}")]
    [DataRow("rule-providers", "{\"providers\":null}")]
    [DataRow("configuration", "{\"port\":7890,\"socks-port\":7891,\"redir-port\":0,\"tproxy-port\":0,\"mixed-port\":7892,\"allow-lan\":true,\"mode\":null}")]
    [DataRow("configuration", "{\"port\":7890,\"socks-port\":7891,\"redir-port\":0,\"tproxy-port\":0,\"mixed-port\":7892,\"allow-lan\":true,\"mode\":\" \"}")]
    [DataRow("configuration", "{\"socks-port\":7891,\"redir-port\":0,\"tproxy-port\":0,\"mixed-port\":7892,\"allow-lan\":true,\"mode\":\"rule\"}")]
    [DataRow("configuration", "{\"port\":7890,\"redir-port\":0,\"tproxy-port\":0,\"mixed-port\":7892,\"allow-lan\":true,\"mode\":\"rule\"}")]
    [DataRow("configuration", "{\"port\":7890,\"socks-port\":7891,\"tproxy-port\":0,\"mixed-port\":7892,\"allow-lan\":true,\"mode\":\"rule\"}")]
    [DataRow("configuration", "{\"port\":7890,\"socks-port\":7891,\"redir-port\":0,\"mixed-port\":7892,\"allow-lan\":true,\"mode\":\"rule\"}")]
    [DataRow("configuration", "{\"port\":7890,\"socks-port\":7891,\"redir-port\":0,\"tproxy-port\":0,\"allow-lan\":true,\"mode\":\"rule\"}")]
    [DataRow("configuration", "{\"port\":7890,\"socks-port\":7891,\"redir-port\":0,\"tproxy-port\":0,\"mixed-port\":7892,\"mode\":\"rule\"}")]
    [DataRow("configuration", "{\"port\":7890,\"socks-port\":7891,\"redir-port\":0,\"tproxy-port\":0,\"mixed-port\":7892,\"allow-lan\":true}")]
    [DataRow("smart", "{\"weights\":null}")]
    [DataRow("statistics", "{\"outbounds\":null}")]
    [DataRow("proxies", "{\"proxies\":{\"x\":{}}}")]
    [DataRow("proxies", "{\"proxies\":{\"x\":null}}")]
    [DataRow("proxy-providers", "{\"providers\":{\"P\":{\"proxies\":null,\"vehicleType\":\"HTTP\"}}}")]
    [DataRow("proxy-providers", "{\"providers\":{\"P\":null}}")]
    [DataRow("proxy-providers", "{\"providers\":{\"P\":{\"proxies\":[null],\"vehicleType\":\"HTTP\"}}}")]
    [DataRow("rules", "{\"rules\":[{\"type\":null,\"payload\":\"\",\"proxy\":\"MATCH\"}]}")]
    [DataRow("rules", "{\"rules\":[{\"type\":\"MATCH\",\"payload\":null,\"proxy\":\"DIRECT\"}]}")]
    [DataRow("rules", "{\"rules\":[null]}")]
    [DataRow("rule-providers", "{\"providers\":{\"P\":{\"type\":\"Rule\",\"vehicleType\":null}}}")]
    [DataRow("rule-providers", "{\"providers\":{\"P\":null}}")]
    [DataRow("smart", "{\"weights\":{\"Auto\":[{\"Name\":null,\"Rank\":\"1\",\"Weight\":1}]}}")]
    [DataRow("smart", "{\"weights\":{\"Auto\":[{\"Name\":\"Node A\",\"Rank\":\"1\"}]}}")]
    [DataRow("smart", "{\"weights\":{\"Auto\":null}}")]
    [DataRow("smart", "{\"weights\":{\"Auto\":[null]}}")]
    [DataRow("statistics", "{\"outbounds\":[{\"name\":null,\"totalConns\":1,\"activeConns\":1,\"upload\":1,\"download\":1,\"errors\":0}]}")]
    [DataRow("statistics", "{\"outbounds\":[{\"name\":\"Proxy\",\"activeConns\":1,\"upload\":1,\"download\":1,\"errors\":0}]}")]
    [DataRow("statistics", "{\"outbounds\":[{\"name\":\"Proxy\",\"totalConns\":1,\"upload\":1,\"download\":1,\"errors\":0}]}")]
    [DataRow("statistics", "{\"outbounds\":[{\"name\":\"Proxy\",\"totalConns\":1,\"activeConns\":1,\"download\":1,\"errors\":0}]}")]
    [DataRow("statistics", "{\"outbounds\":[{\"name\":\"Proxy\",\"totalConns\":1,\"activeConns\":1,\"upload\":1,\"errors\":0}]}")]
    [DataRow("statistics", "{\"outbounds\":[{\"name\":\"Proxy\",\"totalConns\":1,\"activeConns\":1,\"upload\":1,\"download\":1}]}")]
    [DataRow("statistics", "{\"outbounds\":[null]}")]
    public async Task MissingNullOrBlankRequiredRestFieldsAreRejected(
        string operation,
        string payload)
    {
        CapabilityRegistry capabilities = new();
        using RecordingHttpMessageHandler handler = new(_ => JsonResponse(payload));
        using HttpClient httpClient = new(handler);
        ClashRestClient client = CreateClient(httpClient, capabilities);

        Func<Task> call = operation switch
        {
            "version" => async () => _ = await client.GetVersionAsync(),
            "proxies" => async () => _ = await client.GetProxiesAsync(),
            "proxy-providers" => async () => _ = await client.GetProxyProvidersAsync(),
            "rules" => async () => _ = await client.GetRulesAsync(),
            "rule-providers" => async () => _ = await client.GetRuleProvidersAsync(),
            "configuration" => async () => _ = await client.GetConfigurationAsync(),
            "smart" => async () => _ = await client.GetSmartWeightsAsync(),
            "statistics" => async () => _ = await client.GetHonkRuntimeStatisticsAsync(),
            _ => throw new InvalidOperationException($"Unknown operation: {operation}"),
        };

        _ = await Assert.ThrowsExactlyAsync<ClashProtocolException>(call);

        ClashCapability? optionalCapability = operation switch
        {
            "smart" => ClashCapability.SmartWeights,
            "statistics" => ClashCapability.RuntimeStatistics,
            _ => null,
        };
        if (optionalCapability.HasValue)
        {
            Assert.AreEqual(
                CapabilityObservation.Unknown,
                capabilities.GetObservation(optionalCapability.Value));
        }
    }

    [TestMethod]
    public async Task ErrorResponseBodyRedactsStructuredSecretsAndNestedUrls()
    {
        using RecordingHttpMessageHandler handler = new(_ =>
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    """
                    {
                      "message": "request failed for secret with spaces",
                      "token": "token-value",
                      "nested": {
                        "url": "https://user:password@example.test/check?token=nested-token"
                      },
                      "safe": "kept"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            });
        using HttpClient httpClient = new(handler);
        ClashRestClient client = CreateClient(httpClient, new CapabilityRegistry());

        ClashApiException exception = await Assert.ThrowsExactlyAsync<ClashApiException>(
            () => client.GetVersionAsync());

        Assert.IsNotNull(exception.ResponseBody);
        Assert.IsFalse(exception.ResponseBody.Contains("secret with spaces", StringComparison.Ordinal));
        Assert.IsFalse(exception.ResponseBody.Contains("token-value", StringComparison.Ordinal));
        Assert.IsFalse(exception.ResponseBody.Contains("user:password", StringComparison.Ordinal));
        using JsonDocument body = JsonDocument.Parse(exception.ResponseBody);
        Assert.AreEqual(
            "request failed for [redacted]",
            body.RootElement.GetProperty("message").GetString());
        Assert.AreEqual("[redacted]", body.RootElement.GetProperty("token").GetString());
        Assert.AreEqual(
            "[redacted]",
            body.RootElement.GetProperty("nested").GetProperty("url").GetString());
        Assert.AreEqual("kept", body.RootElement.GetProperty("safe").GetString());
    }

    [TestMethod]
    public async Task UnstructuredErrorResponseBodyIsNotRetained()
    {
        using RecordingHttpMessageHandler handler = new(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent(
                    "token=unstructured-secret",
                    Encoding.UTF8,
                    "text/plain"),
            });
        using HttpClient httpClient = new(handler);
        ClashRestClient client = CreateClient(httpClient, new CapabilityRegistry());

        ClashApiException exception = await Assert.ThrowsExactlyAsync<ClashApiException>(
            () => client.GetVersionAsync());

        Assert.IsNull(exception.ResponseBody);
    }

    [TestMethod]
    public async Task HonkStatisticsMapWireFixtureAndUseStatsRoute()
    {
        CapabilityRegistry capabilities = new();
        using RecordingHttpMessageHandler handler = new(_ => JsonResponse(
            """
            {
              "outbounds": [
                {
                  "name": "Node A",
                  "totalConns": 21,
                  "activeConns": 4,
                  "upload": 100,
                  "download": 200,
                  "errors": 2
                }
              ],
              "futureField": { "ignored": true }
            }
            """));
        using HttpClient httpClient = new(handler);
        ClashRestClient client = CreateClient(httpClient, capabilities);

        HonkRuntimeStatistics statistics = await client.GetHonkRuntimeStatisticsAsync();

        HonkOutboundStatistics outbound = statistics.Outbounds.Single();
        Assert.AreEqual("Node A", outbound.Name);
        Assert.AreEqual(21L, outbound.TotalConnections);
        Assert.AreEqual(4L, outbound.ActiveConnections);
        Assert.AreEqual(100L, outbound.Upload);
        Assert.AreEqual(200L, outbound.Download);
        Assert.AreEqual(2L, outbound.Errors);
        Assert.AreEqual(HttpMethod.Get, handler.LastRequest.Method);
        Assert.AreEqual("/api/v1/stats", handler.LastRequest.Uri.AbsolutePath);
        Assert.AreEqual(
            CapabilitySupport.Supported,
            capabilities.GetObservation(ClashCapability.RuntimeStatistics).Support);
    }

    [TestMethod]
    [DataRow(404, CapabilityEvidenceKind.EndpointNotFound)]
    [DataRow(405, CapabilityEvidenceKind.MethodNotAllowed)]
    public async Task StaticEndpointNotFoundAndMethodNotAllowedMarkCapabilityUnsupported(
        int statusCode,
        CapabilityEvidenceKind expectedEvidence)
    {
        CapabilityRegistry capabilities = new();
        using RecordingHttpMessageHandler handler = new(_ => new HttpResponseMessage((HttpStatusCode)statusCode)
        {
            Content = new StringContent("{\"message\":\"not supported\"}", Encoding.UTF8, "application/json"),
        });
        using HttpClient httpClient = new(handler);
        ClashRestClient client = CreateClient(httpClient, capabilities);

        ClashApiException exception = await Assert.ThrowsExactlyAsync<ClashApiException>(
            () => client.FlushSmartWeightsAsync());

        Assert.AreEqual((HttpStatusCode)statusCode, exception.StatusCode);
        CapabilityObservation observation = capabilities.GetObservation(
            ClashCapability.SmartWeightReset);
        Assert.AreEqual(CapabilitySupport.Unsupported, observation.Support);
        Assert.AreEqual(expectedEvidence, observation.Evidence);
        Assert.AreEqual(ObservedAt, observation.ObservedAt);
        Assert.AreEqual($"HTTP {statusCode}", observation.Detail);
    }

    [TestMethod]
    [DataRow(401)]
    [DataRow(403)]
    [DataRow(500)]
    [DataRow(503)]
    public async Task AuthenticationAndTransientFailuresPreserveKnownCapability(int statusCode)
    {
        CapabilityRegistry capabilities = new();
        CapabilityObservation expected = new(
            CapabilitySupport.Supported,
            CapabilityEvidenceKind.SuccessfulCall,
            ObservedAt,
            "previous success");
        capabilities.Observe(
            ClashCapability.SmartWeightReset,
            expected.Support,
            expected.Evidence,
            expected.ObservedAt,
            expected.Detail);
        int changeCount = 0;
        capabilities.Changed += (_, _) => changeCount++;
        using RecordingHttpMessageHandler handler = new(_ =>
            new HttpResponseMessage((HttpStatusCode)statusCode));
        using HttpClient httpClient = new(handler);
        ClashRestClient client = CreateClient(httpClient, capabilities);

        _ = await Assert.ThrowsAsync<ClashApiException>(
            () => client.FlushSmartWeightsAsync());

        Assert.AreEqual(
            expected,
            capabilities.GetObservation(ClashCapability.SmartWeightReset));
        Assert.AreEqual(0, changeCount);
    }

    [TestMethod]
    [DataRow("provider")]
    [DataRow("rule")]
    [DataRow("connection")]
    public async Task DynamicResourceNotFoundDoesNotMarkWholeCapabilityUnsupported(string operation)
    {
        CapabilityRegistry capabilities = new();
        using RecordingHttpMessageHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using HttpClient httpClient = new(handler);
        ClashRestClient client = CreateClient(httpClient, capabilities);

        ClashCapability capability = operation switch
        {
            "provider" => ClashCapability.ProviderProxyUpdate,
            "rule" => ClashCapability.RuleDisableByIdentifier,
            "connection" => ClashCapability.SmartConnectionBlock,
            _ => throw new InvalidOperationException($"Unknown operation: {operation}"),
        };

        _ = await Assert.ThrowsExactlyAsync<ClashApiException>(() => operation switch
        {
            "provider" => client.UpdateProxyProviderAsync("missing-provider"),
            "rule" => client.ToggleRuleDisabledByIdentifierAsync("missing-uuid"),
            "connection" => client.BlockSmartConnectionAsync("missing-connection"),
            _ => throw new InvalidOperationException($"Unknown operation: {operation}"),
        });

        Assert.AreEqual(CapabilityObservation.Unknown, capabilities.GetObservation(capability));
    }

    [TestMethod]
    public async Task DynamicResourceMethodNotAllowedStillMarksCapabilityUnsupported()
    {
        CapabilityRegistry capabilities = new();
        using RecordingHttpMessageHandler handler = new(_ =>
            new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
        using HttpClient httpClient = new(handler);
        ClashRestClient client = CreateClient(httpClient, capabilities);

        _ = await Assert.ThrowsExactlyAsync<ClashApiException>(
            () => client.UpdateProxyProviderAsync("Provider A"));

        CapabilityObservation observation = capabilities.GetObservation(
            ClashCapability.ProviderProxyUpdate);
        Assert.AreEqual(CapabilitySupport.Unsupported, observation.Support);
        Assert.AreEqual(CapabilityEvidenceKind.MethodNotAllowed, observation.Evidence);
    }

    [TestMethod]
    public async Task DefaultCancellationTokenStillUsesConfiguredOperationTimeout()
    {
        using NeverCompletingHttpMessageHandler handler = new();
        using HttpClient httpClient = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
        ClashRestClient client = CreateClient(
            httpClient,
            new CapabilityRegistry(),
            new ClashHttpOptions { OperationTimeout = TimeSpan.FromMilliseconds(50) },
            TimeProvider.System);

        ClashApiException exception = await Assert.ThrowsExactlyAsync<ClashApiException>(
            () => client.GetVersionAsync());

        Assert.AreEqual("The Clash API request timed out.", exception.Message);
    }

    [TestMethod]
    public async Task DelayRequestExtendsShorterDefaultHttpOperationTimeout()
    {
        using DelayedJsonHttpMessageHandler handler = new(TimeSpan.FromMilliseconds(75));
        using HttpClient httpClient = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
        ClashRestClient client = CreateClient(
            httpClient,
            new CapabilityRegistry(),
            new ClashHttpOptions { OperationTimeout = TimeSpan.FromMilliseconds(25) },
            TimeProvider.System);

        ProxyDelay delay = await client.MeasureProxyDelayAsync(
            "Node A",
            new ProxyDelayRequest(
                new Uri("https://probe.example/generate_204"),
                TimeSpan.FromMilliseconds(100)));

        Assert.AreEqual(42, delay.Milliseconds);
    }

    [TestMethod]
    public async Task HttpRequestFailureDoesNotRetainSensitiveUriInInnerException()
    {
        using ThrowingHttpMessageHandler handler = new();
        using HttpClient httpClient = new(handler);
        ClashRestClient client = CreateClient(httpClient, new CapabilityRegistry());
        Uri target = new(
            "https://nested-user:nested-password@probe.example/check?token=nested-token");

        ClashApiException exception = await Assert.ThrowsExactlyAsync<ClashApiException>(() =>
            client.MeasureProxyDelayAsync(
                "Node A",
                new ProxyDelayRequest(target, TimeSpan.FromSeconds(1))));

        Assert.IsInstanceOfType<HttpRequestException>(exception.InnerException);
        Assert.IsFalse(exception.ToString().Contains("nested-user", StringComparison.Ordinal));
        Assert.IsFalse(exception.ToString().Contains("nested-password", StringComparison.Ordinal));
        Assert.IsFalse(exception.ToString().Contains("nested-token", StringComparison.Ordinal));
        Assert.IsNotNull(exception.RequestUri);
        Assert.AreEqual("[redacted]", ParseQuery(exception.RequestUri)["url"]);
    }

    [TestMethod]
    public void ApiExceptionRedactsOuterUserInfoAndSensitiveQueryValues()
    {
        Uri requestUri = new(
            "https://outer-user:outer-password@controller.example/api?" +
            "token=token-value&secret=secret-value&password=password-value&" +
            "url=https%3A%2F%2Fnested.example%2F%3Ftoken%3Dnested-value&other=visible");

        ClashApiException exception = new("Request failed.", requestUri: requestUri);

        Assert.IsNotNull(exception.RequestUri);
        Assert.AreEqual(string.Empty, exception.RequestUri.UserInfo);
        Dictionary<string, string> query = ParseQuery(exception.RequestUri);
        Assert.AreEqual("[redacted]", query["token"]);
        Assert.AreEqual("[redacted]", query["secret"]);
        Assert.AreEqual("[redacted]", query["password"]);
        Assert.AreEqual("[redacted]", query["url"]);
        Assert.AreEqual("visible", query["other"]);
    }

    [TestMethod]
    public void ApiExceptionRedactionMatchesOnlyCompleteSensitiveNamesIgnoringCaseAndEscaping()
    {
        Uri requestUri = new(
            "https://user:password@controller.example/base/path?" +
            "TOKEN=one&%73ecret=two&PASSWORD&Url=three&" +
            "access_token=visible-token&secretary=visible-secret&" +
            "passwordHint=visible-hint&return_url=visible-url#section");

        ClashApiException exception = new("Request failed.", requestUri: requestUri);

        Assert.IsNotNull(exception.RequestUri);
        Assert.AreEqual(string.Empty, exception.RequestUri.UserInfo);
        Assert.AreEqual("/base/path", exception.RequestUri.AbsolutePath);
        Assert.AreEqual("#section", exception.RequestUri.Fragment);
        Dictionary<string, string> query = ParseQuery(exception.RequestUri);
        Assert.AreEqual("[redacted]", query["TOKEN"]);
        Assert.AreEqual("[redacted]", query["secret"]);
        Assert.AreEqual("[redacted]", query["PASSWORD"]);
        Assert.AreEqual("[redacted]", query["Url"]);
        Assert.AreEqual("visible-token", query["access_token"]);
        Assert.AreEqual("visible-secret", query["secretary"]);
        Assert.AreEqual("visible-hint", query["passwordHint"]);
        Assert.AreEqual("visible-url", query["return_url"]);
    }

    [TestMethod]
    public void ApiExceptionRedactionLeavesQuerylessUriStableApartFromUserInfo()
    {
        Uri requestUri = new("https://user:password@controller.example:8443/api/v1/#section");

        ClashApiException exception = new("Request failed.", requestUri: requestUri);

        Assert.IsNotNull(exception.RequestUri);
        Assert.AreEqual("https://controller.example:8443/api/v1/#section", exception.RequestUri.AbsoluteUri);
    }

    private static ClashRestClient CreateClient(
        HttpClient httpClient,
        ICapabilityRegistry capabilities,
        ClashHttpOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        BackendProfile profile = new(
            Guid.Parse("918c524d-8626-4f77-9e39-eb988493980e"),
            "Test backend",
            BackendEndpoint.Create("https://controller.example/api/v1/"));
        return new ClashRestClient(
            httpClient,
            profile,
            new BackendCredential("secret with spaces"),
            capabilities,
            timeProvider ?? new FixedTimeProvider(ObservedAt),
            options);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static Dictionary<string, string> ParseQuery(Uri uri)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (string pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=', 2);
            values[Uri.UnescapeDataString(parts[0])] = parts.Length == 2
                ? Uri.UnescapeDataString(parts[1])
                : string.Empty;
        }

        return values;
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class NeverCompletingHttpMessageHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return null!;
        }
    }

    private sealed class DelayedJsonHttpMessageHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return JsonResponse("{\"delay\":42}");
        }
    }

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException($"Failed to send {request.RequestUri}");
    }
}
