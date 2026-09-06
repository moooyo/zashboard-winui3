using Zashboard.Core.Clash;

namespace Zashboard.App.Services;

public sealed record SessionLogEntry(
    long Sequence,
    DateTimeOffset Timestamp,
    ClashLogLevel Level,
    string RawLevel,
    string Message);

public sealed class SessionLogsChangedEventArgs(
    IReadOnlyList<SessionLogEntry> added,
    int removedFromStart,
    bool isReset = false,
    long droppedBeforeDisplay = 0) : EventArgs
{
    public IReadOnlyList<SessionLogEntry> Added { get; } = added;

    public int RemovedFromStart { get; } = Math.Max(0, removedFromStart);

    public bool IsReset { get; } = isReset;

    public long DroppedBeforeDisplay { get; } = Math.Max(0, droppedBeforeDisplay);
}

public enum BackendCredentialUpdate
{
    Keep,
    Replace,
    Remove,
}

public enum BackendProfilesLoadState
{
    NotLoaded,
    Empty,
    Loaded,
    Failed,
}

public sealed record BackendSaveRequest(
    Guid? ProfileId,
    string Name,
    Uri ControllerUri,
    string Secret,
    BackendCredentialUpdate CredentialUpdate)
{
    public override string ToString() =>
        $"{nameof(BackendSaveRequest)} {{ ProfileId = {ProfileId}, Name = {Name}, " +
        $"ControllerUri = {ControllerUri}, Secret = <redacted>, " +
        $"CredentialUpdate = {CredentialUpdate} }}";
}
