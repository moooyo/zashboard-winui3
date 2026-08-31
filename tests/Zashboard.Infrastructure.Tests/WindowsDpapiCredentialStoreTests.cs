using System.ComponentModel;
using System.Text;
using Zashboard.Core.Backends;
using Zashboard.Infrastructure.Persistence;

namespace Zashboard.Infrastructure.Tests;

[TestClass]
public sealed class WindowsDpapiCredentialStoreTests
{
    [TestMethod]
    public async Task SetRejectsCredentialLargerThanPersistenceLimitBeforeWriting()
    {
        string rootDirectory = Path.Combine(
            Path.GetTempPath(),
            $"Zashboard.Infrastructure.Tests.{Guid.NewGuid():N}");
        Guid profileId = Guid.NewGuid();

        try
        {
            using WindowsDpapiCredentialStore store = new(
                new InfrastructureStorageOptions(rootDirectory));
            BackendCredential credential = new(new string('s', (1024 * 1024) + 1));

            _ = await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
                await store.SetAsync(profileId, credential));

            Assert.IsFalse(Directory.Exists(Path.Combine(rootDirectory, "credentials")));
        }
        finally
        {
            if (Directory.Exists(rootDirectory))
            {
                Directory.Delete(rootDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task GetRejectsCredentialFileLargerThanPersistenceLimit()
    {
        string rootDirectory = Path.Combine(
            Path.GetTempPath(),
            $"Zashboard.Infrastructure.Tests.{Guid.NewGuid():N}");
        Guid profileId = Guid.NewGuid();
        string credentialDirectory = Path.Combine(rootDirectory, "credentials");

        try
        {
            Directory.CreateDirectory(credentialDirectory);
            string path = Path.Combine(credentialDirectory, $"{profileId:N}.bin");
            await using (FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write))
            {
                stream.SetLength((1024 * 1024) + 1L);
            }

            using WindowsDpapiCredentialStore store = new(
                new InfrastructureStorageOptions(rootDirectory));

            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await store.GetAsync(profileId));
        }
        finally
        {
            if (Directory.Exists(rootDirectory))
            {
                Directory.Delete(rootDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [TestCategory("Windows")]
    public async Task SetGetAndDeleteRoundTripsCredentialForCurrentUser()
    {
        string rootDirectory = Path.Combine(
            Path.GetTempPath(),
            $"Zashboard.Infrastructure.Tests.{Guid.NewGuid():N}");
        Guid profileId = Guid.NewGuid();

        try
        {
            using WindowsDpapiCredentialStore store = new(
                new InfrastructureStorageOptions(rootDirectory));

            Assert.IsFalse(await store.ExistsAsync(profileId));
            await store.SetAsync(profileId, new BackendCredential("test-secret"));
            Assert.IsTrue(await store.ExistsAsync(profileId));
            string credentialPath = Path.Combine(
                rootDirectory,
                "credentials",
                $"{profileId:N}.bin");
            byte[] envelope = await File.ReadAllBytesAsync(credentialPath);
            CollectionAssert.AreEqual("ZBC1"u8.ToArray(), envelope[..4]);
            Assert.IsFalse(
                Encoding.UTF8.GetString(envelope).Contains(
                    "test-secret",
                    StringComparison.Ordinal));
            Assert.IsEmpty(Directory.GetFiles(rootDirectory, "*.tmp", SearchOption.AllDirectories));

            BackendCredential? credential = await store.GetAsync(profileId);

            Assert.IsNotNull(credential);
            Assert.AreEqual("test-secret", credential.Secret);

            await store.DeleteAsync(profileId);
            Assert.IsFalse(await store.ExistsAsync(profileId));
            Assert.IsNull(await store.GetAsync(profileId));
        }
        finally
        {
            if (Directory.Exists(rootDirectory))
            {
                Directory.Delete(rootDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [TestCategory("Windows")]
    public async Task TamperedCiphertextIsRejected()
    {
        string rootDirectory = Path.Combine(
            Path.GetTempPath(),
            $"Zashboard.Infrastructure.Tests.{Guid.NewGuid():N}");
        Guid profileId = Guid.NewGuid();

        try
        {
            using WindowsDpapiCredentialStore store = new(
                new InfrastructureStorageOptions(rootDirectory));
            await store.SetAsync(profileId, new BackendCredential("test-secret"));
            string credentialPath = Path.Combine(
                rootDirectory,
                "credentials",
                $"{profileId:N}.bin");
            byte[] envelope = await File.ReadAllBytesAsync(credentialPath);
            envelope[^1] ^= 0x5a;
            await File.WriteAllBytesAsync(credentialPath, envelope);

            _ = await Assert.ThrowsExactlyAsync<Win32Exception>(async () =>
                await store.GetAsync(profileId));
        }
        finally
        {
            if (Directory.Exists(rootDirectory))
            {
                Directory.Delete(rootDirectory, recursive: true);
            }
        }
    }
}
