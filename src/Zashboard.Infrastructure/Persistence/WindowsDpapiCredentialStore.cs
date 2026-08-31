using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Zashboard.Core.Backends;

namespace Zashboard.Infrastructure.Persistence;

public sealed partial class WindowsDpapiCredentialStore : IBackendCredentialStore, IDisposable
{
    private const uint CryptProtectUiForbidden = 0x1;
    private const int MaximumCredentialFileSize = 1024 * 1024;
    private const int MaximumCredentialPlaintextSize = MaximumCredentialFileSize - 1024;

    private static readonly byte[] FileHeader = "ZBC1"u8.ToArray();
    private static readonly byte[] OptionalEntropy = SHA256.HashData(
        "Zashboard.BackendCredential.v1"u8);

    private readonly string _directoryPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposeState;

    public WindowsDpapiCredentialStore(InfrastructureStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _directoryPath = options.GetCredentialDirectoryPath();
    }

    public async ValueTask<bool> ExistsAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposeState != 0, this);
        ValidateProfileId(profileId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return File.Exists(GetCredentialPath(profileId));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<BackendCredential?> GetAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposeState != 0, this);
        ValidateProfileId(profileId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = GetCredentialPath(profileId);
            FileStream file;
            try
            {
                file = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }

            await using (file)
            {
                long fileLength = file.Length;
                if (fileLength <= FileHeader.Length || fileLength > MaximumCredentialFileSize)
                {
                    throw new InvalidDataException("The encrypted backend credential has an invalid size.");
                }

                byte[] envelope = GC.AllocateUninitializedArray<byte>(checked((int)fileLength));
                try
                {
                    await file.ReadExactlyAsync(envelope, cancellationToken).ConfigureAwait(false);
                    if (!envelope.AsSpan().StartsWith(FileHeader))
                    {
                        throw new InvalidDataException("The encrypted backend credential has an unknown format.");
                    }

                    byte[] protectedData = envelope[FileHeader.Length..];
                    byte[] plaintext = Unprotect(protectedData);
                    try
                    {
                        return new BackendCredential(Encoding.UTF8.GetString(plaintext));
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(plaintext);
                        CryptographicOperations.ZeroMemory(protectedData);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(envelope);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SetAsync(
        Guid profileId,
        BackendCredential credential,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposeState != 0, this);
        ValidateProfileId(profileId);
        ArgumentNullException.ThrowIfNull(credential);
        cancellationToken.ThrowIfCancellationRequested();

        int plaintextSize = Encoding.UTF8.GetByteCount(credential.Secret);
        if (plaintextSize > MaximumCredentialPlaintextSize)
        {
            throw new ArgumentException(
                "The backend credential exceeds the maximum supported size.",
                nameof(credential));
        }

        byte[] plaintext = Encoding.UTF8.GetBytes(credential.Secret);
        byte[] protectedData;
        try
        {
            protectedData = Protect(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        byte[] envelope;
        try
        {
            if (protectedData.Length > MaximumCredentialFileSize - FileHeader.Length)
            {
                throw new ArgumentException(
                    "The protected backend credential exceeds the maximum supported size.",
                    nameof(credential));
            }

            envelope = new byte[FileHeader.Length + protectedData.Length];
            FileHeader.CopyTo(envelope, 0);
            protectedData.CopyTo(envelope, FileHeader.Length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedData);
        }

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(_directoryPath);
                string path = GetCredentialPath(profileId);
                string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
                try
                {
                    await using (FileStream stream = new(
                        temporaryPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        4096,
                        FileOptions.Asynchronous | FileOptions.WriteThrough))
                    {
                        await stream.WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
                        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        stream.Flush(flushToDisk: true);
                    }

                    File.Move(temporaryPath, path, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelope);
        }
    }

    public async ValueTask DeleteAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposeState != 0, this);
        ValidateProfileId(profileId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = GetCredentialPath(profileId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0)
        {
            _gate.Dispose();
        }
    }

    private static byte[] Protect(byte[] plaintext)
    {
        DataBlob input = AllocateBlob(plaintext);
        DataBlob entropy = AllocateBlob(OptionalEntropy);
        DataBlob output = default;
        try
        {
            if (!CryptProtectData(
                in input,
                null,
                in entropy,
                IntPtr.Zero,
                IntPtr.Zero,
                CryptProtectUiForbidden,
                out output))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            return CopyBlob(output);
        }
        finally
        {
            FreeInputBlob(input);
            FreeInputBlob(entropy);
            FreeOutputBlob(output);
        }
    }

    private static byte[] Unprotect(byte[] protectedData)
    {
        DataBlob input = AllocateBlob(protectedData);
        DataBlob entropy = AllocateBlob(OptionalEntropy);
        DataBlob output = default;
        try
        {
            if (!CryptUnprotectData(
                in input,
                IntPtr.Zero,
                in entropy,
                IntPtr.Zero,
                IntPtr.Zero,
                CryptProtectUiForbidden,
                out output))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            return CopyBlob(output);
        }
        finally
        {
            FreeInputBlob(input);
            FreeInputBlob(entropy);
            FreeOutputBlob(output);
        }
    }

    private static DataBlob AllocateBlob(byte[] data)
    {
        IntPtr pointer = Marshal.AllocHGlobal(Math.Max(data.Length, 1));
        if (data.Length > 0)
        {
            Marshal.Copy(data, 0, pointer, data.Length);
        }

        return new DataBlob(data.Length, pointer);
    }

    private static byte[] CopyBlob(DataBlob blob)
    {
        if (blob.Size < 0 || blob.Data == IntPtr.Zero)
        {
            throw new InvalidDataException("Windows DPAPI returned an invalid data blob.");
        }

        byte[] result = new byte[blob.Size];
        if (blob.Size > 0)
        {
            Marshal.Copy(blob.Data, result, 0, blob.Size);
        }

        return result;
    }

    private static void FreeInputBlob(DataBlob blob)
    {
        if (blob.Data == IntPtr.Zero)
        {
            return;
        }

        ZeroMemory(blob.Data, blob.Size);
        Marshal.FreeHGlobal(blob.Data);
    }

    private static void FreeOutputBlob(DataBlob blob)
    {
        if (blob.Data == IntPtr.Zero)
        {
            return;
        }

        ZeroMemory(blob.Data, blob.Size);
        _ = LocalFree(blob.Data);
    }

    private static unsafe void ZeroMemory(IntPtr pointer, int length)
    {
        if (length > 0)
        {
            new Span<byte>(pointer.ToPointer(), length).Clear();
        }
    }

    private string GetCredentialPath(Guid profileId) =>
        Path.Combine(_directoryPath, $"{profileId:N}.bin");

    private static void ValidateProfileId(Guid profileId)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException(
                "The backend profile identifier must not be empty.",
                nameof(profileId));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public DataBlob(int size, IntPtr data)
        {
            Size = size;
            Data = data;
        }

        public int Size;

        public IntPtr Data;
    }

    [LibraryImport("Crypt32.dll", EntryPoint = "CryptProtectData", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(
        in DataBlob dataIn,
        string? description,
        in DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [LibraryImport("Crypt32.dll", EntryPoint = "CryptUnprotectData", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(
        in DataBlob dataIn,
        IntPtr description,
        in DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [LibraryImport("Kernel32.dll", EntryPoint = "LocalFree")]
    private static partial IntPtr LocalFree(IntPtr memory);
}
