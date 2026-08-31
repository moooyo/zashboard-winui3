namespace Zashboard.App.ViewModels;

internal readonly record struct ListenerSettingsDraft(
    double HttpPort,
    double SocksPort,
    double RedirPort,
    double TProxyPort,
    double MixedPort,
    bool AllowLan);

internal sealed class ListenerSettingsDraftState
{
    private long? _sessionRevision;
    private ListenerSettingsDraft? _draft;

    public bool IsDirty { get; private set; }

    public bool ShouldSynchronize(long sessionRevision)
    {
        if (_sessionRevision != sessionRevision)
        {
            _sessionRevision = sessionRevision;
            IsDirty = false;
            _draft = null;
            return true;
        }

        return !IsDirty;
    }

    public void MarkEdited() => IsDirty = true;

    public void MarkEdited(long sessionRevision, ListenerSettingsDraft draft)
    {
        if (_sessionRevision != sessionRevision)
        {
            _sessionRevision = sessionRevision;
        }

        _draft = draft;
        IsDirty = true;
    }

    public ListenerSettingsDraft Resolve(
        long sessionRevision,
        ListenerSettingsDraft authoritative)
    {
        if (ShouldSynchronize(sessionRevision))
        {
            _draft = authoritative;
        }

        return _draft ?? authoritative;
    }

    public void MarkSaved()
    {
        IsDirty = false;
        _draft = null;
    }
}

internal static class SettingsFormPolicy
{
    public const string ListenerPortValidationMessage =
        "Enter a whole-number port between 0 and 65535.";

    public const string DelayTestUrlValidationMessage =
        "Enter an absolute HTTP or HTTPS URL without user information.";

    public static bool IsValidListenerPort(double value) =>
        !double.IsNaN(value) &&
        value == Math.Truncate(value) &&
        value is >= 0 and <= 65_535;

    public static bool IsValidDelayTestUrl(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        return (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
            string.IsNullOrEmpty(uri.UserInfo);
    }

    public static string DescribeControllerConfiguration(bool hasControlSession) =>
        hasControlSession
            ? "Connected to the controller. Unsupported configuration actions remain unavailable."
            : "Connect to an online controller to use controller configuration actions.";

    public static string DescribeListenerSettings(
        bool hasControlSession,
        bool hasConfiguration,
        bool canPatchConfiguration)
    {
        if (!hasControlSession)
        {
            return "Listener settings are disabled until an online controller is connected.";
        }

        if (!hasConfiguration)
        {
            return "Listener settings are disabled until the controller configuration is loaded.";
        }

        return canPatchConfiguration
            ? "Listener settings are ready to edit."
            : "Listener settings are disabled because this controller does not support configuration patches.";
    }
}
