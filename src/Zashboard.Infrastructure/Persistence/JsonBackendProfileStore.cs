using System.Text.Json;
using Zashboard.Core.Backends;
using Zashboard.Infrastructure.Persistence.Serialization;
using Zashboard.Infrastructure.Persistence.Wire;

namespace Zashboard.Infrastructure.Persistence;

public sealed class JsonBackendProfileStore : IBackendProfileStore, IDisposable
{
    private const int CurrentSchemaVersion = 1;

    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposeState;

    public JsonBackendProfileStore(InfrastructureStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _filePath = options.GetProfileFilePath();
    }

    public async ValueTask<BackendProfileSet> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposeState != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath))
            {
                return new BackendProfileSet();
            }

            await using FileStream stream = new(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            BackendProfilesDocumentDto? document = await JsonSerializer.DeserializeAsync(
                stream,
                PersistenceJsonContext.Default.BackendProfilesDocumentDto,
                cancellationToken).ConfigureAwait(false);

            if (document is null)
            {
                throw new InvalidDataException("The backend profile document is empty.");
            }

            if (document.SchemaVersion is < 1 or > CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"Unsupported backend profile schema version: {document.SchemaVersion}.");
            }

            List<BackendProfile> profiles = [];
            HashSet<Guid> identifiers = [];
            foreach (BackendProfileDto profile in document.Profiles ?? [])
            {
                if (profile is null || profile.Id == Guid.Empty || string.IsNullOrWhiteSpace(profile.Name))
                {
                    throw new InvalidDataException(
                        "The backend profile document contains a profile with missing required data.");
                }

                if (!BackendEndpoint.TryCreate(
                    profile.Endpoint,
                    out BackendEndpoint? endpoint,
                    out string? error))
                {
                    throw new InvalidDataException(
                        $"The backend profile document contains an invalid endpoint: {error}.");
                }

                if (!identifiers.Add(profile.Id))
                {
                    throw new InvalidDataException(
                        $"The backend profile document contains duplicate profile id {profile.Id}.");
                }

                profiles.Add(new BackendProfile(
                    profile.Id,
                    profile.Name,
                    endpoint!,
                    profile.DisableCoreUpgrade,
                    profile.DisableTunMode));
            }

            if (document.ActiveProfileId is Guid activeProfileId && !identifiers.Contains(activeProfileId))
            {
                throw new InvalidDataException("The active backend profile does not exist in the profile list.");
            }

            return new BackendProfileSet
            {
                Profiles = profiles,
                ActiveProfileId = document.ActiveProfileId,
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SaveAsync(
        BackendProfileSet profiles,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposeState != 0, this);
        ArgumentNullException.ThrowIfNull(profiles);
        Validate(profiles);

        BackendProfilesDocumentDto document = new()
        {
            SchemaVersion = CurrentSchemaVersion,
            ActiveProfileId = profiles.ActiveProfileId,
            Profiles = profiles.Profiles.Select(static profile => new BackendProfileDto
            {
                Id = profile.Id,
                Name = profile.Name,
                Endpoint = profile.Endpoint.BaseUri.AbsoluteUri,
                DisableCoreUpgrade = profile.DisableCoreUpgrade,
                DisableTunMode = profile.DisableTunMode,
            }).ToList(),
        };

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (FileStream stream = new(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    16 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(
                        stream,
                        document,
                        PersistenceJsonContext.Default.BackendProfilesDocumentDto,
                        cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, _filePath, overwrite: true);
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0)
        {
            _gate.Dispose();
        }
    }

    private static void Validate(BackendProfileSet profiles)
    {
        HashSet<Guid> identifiers = [];
        foreach (BackendProfile profile in profiles.Profiles)
        {
            ArgumentNullException.ThrowIfNull(profile);
            if (!identifiers.Add(profile.Id))
            {
                throw new ArgumentException(
                    $"Duplicate backend profile id: {profile.Id}.",
                    nameof(profiles));
            }
        }

        if (profiles.ActiveProfileId is Guid activeProfileId && !identifiers.Contains(activeProfileId))
        {
            throw new ArgumentException(
                "The active backend profile must exist in the profile list.",
                nameof(profiles));
        }
    }
}
