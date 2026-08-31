using System.Text.Json;
using Zashboard.Core.Backends;
using Zashboard.Infrastructure.Persistence;

namespace Zashboard.Infrastructure.Tests;

[TestClass]
public sealed class JsonBackendProfileStoreTests
{
    private static readonly Guid PrimaryId = Guid.Parse("6a0b708d-57da-47dc-8f4a-df181fd799e4");
    private static readonly Guid SecondaryId = Guid.Parse("b92d9cca-5c07-411e-9765-09cc8fd4b905");

    [TestMethod]
    public async Task SaveWritesCurrentSchemaWithoutCredentialsAndRoundTripsActiveProfile()
    {
        using TemporaryDirectory directory = new();
        using JsonBackendProfileStore store = CreateStore(directory.Path);
        BackendProfile primary = CreateProfile(PrimaryId, "Primary", "https://controller.example/api/");
        BackendProfile secondary = new(
            SecondaryId,
            "Secondary",
            BackendEndpoint.Create("http://127.0.0.1:9090/"),
            disableCoreUpgrade: true,
            disableTunMode: true);

        await store.SaveAsync(new BackendProfileSet
        {
            Profiles = [primary, secondary],
            ActiveProfileId = SecondaryId,
        });

        string json = await File.ReadAllTextAsync(directory.ProfilePath);
        Assert.IsFalse(json.Contains("credential", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("secret", StringComparison.OrdinalIgnoreCase));
        using (JsonDocument document = JsonDocument.Parse(json))
        {
            Assert.AreEqual(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.AreEqual(SecondaryId, document.RootElement.GetProperty("activeProfileId").GetGuid());
            Assert.AreEqual(2, document.RootElement.GetProperty("profiles").GetArrayLength());
        }

        BackendProfileSet loaded = await store.LoadAsync();
        Assert.AreEqual(SecondaryId, loaded.ActiveProfileId);
        Assert.HasCount(2, loaded.Profiles);
        Assert.AreEqual(primary, loaded.Profiles[0]);
        Assert.AreEqual(secondary, loaded.Profiles[1]);
    }

    [TestMethod]
    public async Task SaveAtomicallyReplacesCompleteDocumentAndLeavesNoTemporaryFiles()
    {
        using TemporaryDirectory directory = new();
        using JsonBackendProfileStore store = CreateStore(directory.Path);
        await store.SaveAsync(new BackendProfileSet
        {
            Profiles = [CreateProfile(PrimaryId, "Before", "http://127.0.0.1:9090/")],
            ActiveProfileId = PrimaryId,
        });

        await store.SaveAsync(new BackendProfileSet
        {
            Profiles = [CreateProfile(SecondaryId, "After", "https://remote.example/base/")],
            ActiveProfileId = SecondaryId,
        });

        using JsonDocument document = JsonDocument.Parse(
            await File.ReadAllTextAsync(directory.ProfilePath));
        JsonElement profile = document.RootElement.GetProperty("profiles")[0];
        Assert.AreEqual(SecondaryId, profile.GetProperty("id").GetGuid());
        Assert.AreEqual("After", profile.GetProperty("name").GetString());
        Assert.AreEqual(SecondaryId, document.RootElement.GetProperty("activeProfileId").GetGuid());
        Assert.IsEmpty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public async Task LoadRejectsUnsupportedSchemaVersions(int schemaVersion)
    {
        using TemporaryDirectory directory = new();
        await directory.WriteProfileDocumentAsync(
            $$"""
            {
              "schemaVersion": {{schemaVersion}},
              "activeProfileId": null,
              "profiles": []
            }
            """);
        using JsonBackendProfileStore store = CreateStore(directory.Path);

        InvalidDataException exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => store.LoadAsync().AsTask());

        StringAssert.Contains(exception.Message, $"schema version: {schemaVersion}");
    }

    [TestMethod]
    public async Task LoadRejectsMalformedJson()
    {
        using TemporaryDirectory directory = new();
        await directory.WriteProfileDocumentAsync("{\"schemaVersion\":1,\"profiles\":[");
        using JsonBackendProfileStore store = CreateStore(directory.Path);

        await Assert.ThrowsExactlyAsync<JsonException>(() => store.LoadAsync().AsTask());
    }

    [TestMethod]
    public async Task LoadRejectsEmptyJsonValue()
    {
        using TemporaryDirectory directory = new();
        await directory.WriteProfileDocumentAsync("null");
        using JsonBackendProfileStore store = CreateStore(directory.Path);

        InvalidDataException exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => store.LoadAsync().AsTask());

        Assert.AreEqual("The backend profile document is empty.", exception.Message);
    }

    [TestMethod]
    public async Task LoadRejectsDuplicateProfileIdentifiers()
    {
        using TemporaryDirectory directory = new();
        await directory.WriteProfileDocumentAsync(
            $$"""
            {
              "schemaVersion": 1,
              "activeProfileId": "{{PrimaryId}}",
              "profiles": [
                { "id": "{{PrimaryId}}", "name": "One", "endpoint": "http://one.example/" },
                { "id": "{{PrimaryId}}", "name": "Two", "endpoint": "http://two.example/" }
              ]
            }
            """);
        using JsonBackendProfileStore store = CreateStore(directory.Path);

        InvalidDataException exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => store.LoadAsync().AsTask());

        StringAssert.Contains(exception.Message, "duplicate profile id");
    }

    [TestMethod]
    public async Task SaveRejectsDuplicateProfileIdentifiersWithoutChangingCommittedFile()
    {
        using TemporaryDirectory directory = new();
        using JsonBackendProfileStore store = CreateStore(directory.Path);
        BackendProfile original = CreateProfile(PrimaryId, "Original", "http://original.example/");
        await store.SaveAsync(new BackendProfileSet
        {
            Profiles = [original],
            ActiveProfileId = PrimaryId,
        });
        string committed = await File.ReadAllTextAsync(directory.ProfilePath);

        ArgumentException exception = await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            store.SaveAsync(new BackendProfileSet
            {
                Profiles = [original, original.With(name: "Duplicate")],
                ActiveProfileId = PrimaryId,
            }).AsTask());

        StringAssert.Contains(exception.Message, "Duplicate backend profile id");
        Assert.AreEqual(committed, await File.ReadAllTextAsync(directory.ProfilePath));
        Assert.IsEmpty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [TestMethod]
    public async Task LoadRejectsActiveProfileIdentifierMissingFromProfiles()
    {
        using TemporaryDirectory directory = new();
        await directory.WriteProfileDocumentAsync(
            $$"""
            {
              "schemaVersion": 1,
              "activeProfileId": "{{SecondaryId}}",
              "profiles": [
                { "id": "{{PrimaryId}}", "name": "Primary", "endpoint": "http://primary.example/" }
              ]
            }
            """);
        using JsonBackendProfileStore store = CreateStore(directory.Path);

        InvalidDataException exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => store.LoadAsync().AsTask());

        Assert.AreEqual(
            "The active backend profile does not exist in the profile list.",
            exception.Message);
    }

    [TestMethod]
    public async Task LoadIgnoresUnknownDocumentAndProfileFields()
    {
        using TemporaryDirectory directory = new();
        await directory.WriteProfileDocumentAsync(
            $$"""
            {
              "schemaVersion": 1,
              "activeProfileId": "{{PrimaryId}}",
              "futureDocumentField": { "enabled": true },
              "profiles": [
                {
                  "id": "{{PrimaryId}}",
                  "name": "Primary",
                  "endpoint": "https://controller.example/api/",
                  "futureProfileField": [1, 2, 3]
                }
              ]
            }
            """);
        using JsonBackendProfileStore store = CreateStore(directory.Path);

        BackendProfileSet loaded = await store.LoadAsync();

        Assert.AreEqual(PrimaryId, loaded.ActiveProfileId);
        Assert.AreEqual("Primary", loaded.Profiles.Single().Name);
        Assert.AreEqual(
            "https://controller.example/api/",
            loaded.Profiles.Single().Endpoint.BaseUri.AbsoluteUri);
    }

    [TestMethod]
    [DataRow("https://user:password@controller.example/api/")]
    [DataRow("https://controller.example/api/?token=secret")]
    [DataRow("https://controller.example/api/#section")]
    public async Task LoadRejectsEndpointWithEmbeddedSensitiveComponents(string endpoint)
    {
        using TemporaryDirectory directory = new();
        await directory.WriteProfileDocumentAsync(
            $$"""
            {
              "schemaVersion": 1,
              "activeProfileId": null,
              "profiles": [
                { "id": "{{PrimaryId}}", "name": "Primary", "endpoint": "{{endpoint}}" }
              ]
            }
            """);
        using JsonBackendProfileStore store = CreateStore(directory.Path);

        InvalidDataException exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => store.LoadAsync().AsTask());

        StringAssert.Contains(exception.Message, "invalid endpoint");
    }

    [TestMethod]
    public async Task SaveRejectsActiveProfileIdentifierMissingFromProfiles()
    {
        using TemporaryDirectory directory = new();
        using JsonBackendProfileStore store = CreateStore(directory.Path);

        ArgumentException exception = await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            store.SaveAsync(new BackendProfileSet
            {
                Profiles = [CreateProfile(PrimaryId, "Primary", "http://primary.example/")],
                ActiveProfileId = SecondaryId,
            }).AsTask());

        StringAssert.Contains(exception.Message, "active backend profile must exist");
        Assert.IsFalse(File.Exists(directory.ProfilePath));
    }

    private static JsonBackendProfileStore CreateStore(string rootDirectory) =>
        new(new InfrastructureStorageOptions(rootDirectory));

    private static BackendProfile CreateProfile(Guid id, string name, string endpoint) =>
        new(id, name, BackendEndpoint.Create(endpoint));

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"Zashboard.Infrastructure.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string ProfilePath => System.IO.Path.Combine(Path, "backends.json");

        public Task WriteProfileDocumentAsync(string content) =>
            File.WriteAllTextAsync(ProfilePath, content);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
