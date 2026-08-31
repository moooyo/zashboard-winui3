namespace Zashboard.Infrastructure.Persistence;

public sealed class InfrastructureStorageOptions
{
    public InfrastructureStorageOptions(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }

    public string ProfileFileName { get; init; } = "backends.json";

    public string CredentialDirectoryName { get; init; } = "credentials";

    public static InfrastructureStorageOptions CreateDefault()
    {
        string localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        return new InfrastructureStorageOptions(Path.Combine(localApplicationData, "Zashboard"));
    }

    internal string GetProfileFilePath()
    {
        ValidateFileName(ProfileFileName, nameof(ProfileFileName));
        return Path.Combine(RootDirectory, ProfileFileName);
    }

    internal string GetCredentialDirectoryPath()
    {
        ValidateFileName(CredentialDirectoryName, nameof(CredentialDirectoryName));
        return Path.Combine(RootDirectory, CredentialDirectoryName);
    }

    private static void ValidateFileName(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value is "." or ".." ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !StringComparer.Ordinal.Equals(value, Path.GetFileName(value)))
        {
            throw new ArgumentException("The value must be a single file-system name.", parameterName);
        }
    }
}
