using Zashboard.Core.Backends;

namespace Zashboard.Core.Tests;

[TestClass]
public sealed class BackendEndpointTests
{
    [TestMethod]
    public void CreateNormalizesSchemeHostDefaultPortAndBasePath()
    {
        BackendEndpoint endpoint = BackendEndpoint.Create("HTTPS://EXAMPLE.COM:443/api/v1");

        Assert.AreEqual("https://example.com/api/v1/", endpoint.BaseUri.AbsoluteUri);
    }

    [TestMethod]
    public void EqualityIgnoresSchemeAndHostCaseButPreservesPathCase()
    {
        BackendEndpoint canonical = BackendEndpoint.Create("https://example.com/TenantA/");
        BackendEndpoint sameEndpoint = BackendEndpoint.Create("HTTPS://EXAMPLE.COM:443/TenantA");
        BackendEndpoint differentPath = BackendEndpoint.Create("https://example.com/tenanta/");

        Assert.AreEqual(canonical, sameEndpoint);
        Assert.AreEqual(canonical.GetHashCode(), sameEndpoint.GetHashCode());
        Assert.AreNotEqual(canonical, differentPath);
    }

    [TestMethod]
    public void FromPartsSupportsBracketedIpv6AndBasePath()
    {
        BackendEndpoint endpoint = BackendEndpoint.FromParts(
            "HTTP",
            "[2001:db8::1]",
            9090,
            " api / v1 ");

        Assert.AreEqual("http://[2001:db8::1]:9090/api/v1/", endpoint.BaseUri.AbsoluteUri);
        Assert.AreEqual("ws://[2001:db8::1]:9090/api/v1/", endpoint.GetWebSocketBaseUri().AbsoluteUri);
    }

    [TestMethod]
    public void ResolvePreservesBasePathAndEncodesEachDynamicSegment()
    {
        BackendEndpoint endpoint = BackendEndpoint.Create("https://controller.example/api/v1/");

        Uri result = endpoint.Resolve("proxies", "Group / East", "node?#1");

        Assert.AreEqual(
            "https://controller.example/api/v1/proxies/Group%20%2F%20East/node%3F%231",
            result.AbsoluteUri);
    }

    [TestMethod]
    public void TryCreateRejectsEmbeddedCredentials()
    {
        bool created = BackendEndpoint.TryCreate(
            "https://user:password@controller.example/api",
            out BackendEndpoint? endpoint,
            out string? error);

        Assert.IsFalse(created);
        Assert.IsNull(endpoint);
        StringAssert.Contains(error, "Credentials");
    }

    [TestMethod]
    [DataRow("https://controller.example/api?token=secret")]
    [DataRow("https://controller.example/api#section")]
    public void TryCreateRejectsQueryAndFragment(string address)
    {
        bool created = BackendEndpoint.TryCreate(address, out BackendEndpoint? endpoint, out string? error);

        Assert.IsFalse(created);
        Assert.IsNull(endpoint);
        StringAssert.Contains(error, "query string or fragment");
    }
}
