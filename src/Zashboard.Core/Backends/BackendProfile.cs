namespace Zashboard.Core.Backends;

public sealed class BackendProfile : IEquatable<BackendProfile>
{
    public BackendProfile(
        Guid id,
        string name,
        BackendEndpoint endpoint,
        bool disableCoreUpgrade = false,
        bool disableTunMode = false)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("The backend profile identifier must not be empty.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Id = id;
        Name = name.Trim();
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        DisableCoreUpgrade = disableCoreUpgrade;
        DisableTunMode = disableTunMode;
    }

    public Guid Id { get; }

    public string Name { get; }

    public BackendEndpoint Endpoint { get; }

    public bool DisableCoreUpgrade { get; }

    public bool DisableTunMode { get; }

    public BackendProfile With(
        string? name = null,
        BackendEndpoint? endpoint = null,
        bool? disableCoreUpgrade = null,
        bool? disableTunMode = null) =>
        new(
            Id,
            name ?? Name,
            endpoint ?? Endpoint,
            disableCoreUpgrade ?? DisableCoreUpgrade,
            disableTunMode ?? DisableTunMode);

    public bool Equals(BackendProfile? other) =>
        other is not null &&
        Id == other.Id &&
        StringComparer.Ordinal.Equals(Name, other.Name) &&
        Endpoint.Equals(other.Endpoint) &&
        DisableCoreUpgrade == other.DisableCoreUpgrade &&
        DisableTunMode == other.DisableTunMode;

    public override bool Equals(object? obj) => Equals(obj as BackendProfile);

    public override int GetHashCode() =>
        HashCode.Combine(Id, Name, Endpoint, DisableCoreUpgrade, DisableTunMode);

    public override string ToString() => Name;
}

public sealed class BackendCredential
{
    public BackendCredential(string? secret)
    {
        Secret = secret ?? string.Empty;
    }

    public string Secret { get; }

    public override string ToString() => "[redacted]";
}
