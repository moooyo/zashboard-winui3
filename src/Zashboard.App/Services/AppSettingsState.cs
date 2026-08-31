using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Windows.Storage;

namespace Zashboard.App.Services;

public sealed partial class AppSettingsState : ObservableObject
{
    private const string DefaultDelayTestUrl = "https://www.gstatic.com/generate_204";

    private readonly ApplicationDataContainer? _storage;
    private bool _startWithWindows;
    private string _closeBehavior = "hideToTray";
    private string _theme = "system";
    private bool _micaBackdrop = true;
    private int _logBufferSize = 5_000;
    private string _delayTestUrl = DefaultDelayTestUrl;
    private int _delayTimeoutMilliseconds = 5_000;

    public AppSettingsState()
        : this(TryGetStorage())
    {
    }

    internal AppSettingsState(ApplicationDataContainer? storage)
    {
        _storage = storage;
        if (_storage is null)
        {
            return;
        }

        _startWithWindows = Read("startWithWindows", _startWithWindows);
        _closeBehavior = Read("closeBehavior", _closeBehavior);
        _theme = Read("theme", _theme);
        _micaBackdrop = Read("micaBackdrop", _micaBackdrop);
        _logBufferSize = Math.Clamp(Read("logBufferSize", _logBufferSize), 500, 50_000);
        _delayTestUrl = NormalizeStoredDelayTestUrl(Read("delayTestUrl", _delayTestUrl));
        _delayTimeoutMilliseconds = Math.Clamp(
            Read("delayTimeoutMilliseconds", _delayTimeoutMilliseconds),
            1_000,
            60_000);
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set => SetAndPersist(ref _startWithWindows, value, "startWithWindows");
    }

    public string CloseBehavior
    {
        get => _closeBehavior;
        set => SetAndPersist(
            ref _closeBehavior,
            NormalizeChoice(value, "hideToTray", "exit"),
            "closeBehavior");
    }

    public string Theme
    {
        get => _theme;
        set => SetAndPersist(
            ref _theme,
            NormalizeChoice(value, "system", "light", "dark"),
            "theme");
    }

    public bool MicaBackdrop
    {
        get => _micaBackdrop;
        set => SetAndPersist(ref _micaBackdrop, value, "micaBackdrop");
    }

    public int LogBufferSize
    {
        get => _logBufferSize;
        set => SetAndPersist(
            ref _logBufferSize,
            Math.Clamp(value, 500, 50_000),
            "logBufferSize");
    }

    public string DelayTestUrl
    {
        get => _delayTestUrl;
        set => SetAndPersist(
            ref _delayTestUrl,
            NormalizeDelayTestUrl(value),
            "delayTestUrl");
    }

    public int DelayTimeoutMilliseconds
    {
        get => _delayTimeoutMilliseconds;
        set => SetAndPersist(
            ref _delayTimeoutMilliseconds,
            Math.Clamp(value, 1_000, 60_000),
            "delayTimeoutMilliseconds");
    }

    public Uri DelayTestUri => new(DelayTestUrl, UriKind.Absolute);

    public TimeSpan DelayTimeout => TimeSpan.FromMilliseconds(DelayTimeoutMilliseconds);

    public void Apply(string key, object value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        switch (key)
        {
            case "startWithWindows" when value is bool enabled:
                StartWithWindows = enabled;
                break;
            case "closeBehavior" when value is string behavior:
                CloseBehavior = behavior;
                break;
            case "theme" when value is string theme:
                Theme = theme;
                break;
            case "micaBackdrop" when value is bool enabled:
                MicaBackdrop = enabled;
                break;
            case "logBufferSize" when TryGetInt32(value, out int size):
                LogBufferSize = size;
                break;
            case "delayTestUrl" when value is string url:
                DelayTestUrl = url;
                break;
            case "delayTimeoutMilliseconds" when TryGetInt32(value, out int timeout):
                DelayTimeoutMilliseconds = timeout;
                break;
            case "delayTimeoutSeconds" when TryGetInt32(value, out int seconds):
                DelayTimeoutMilliseconds = checked(seconds * 1_000);
                break;
            default:
                throw new ArgumentException($"Unsupported setting '{key}' or value type.", nameof(key));
        }
    }

    private static bool TryGetInt32(object value, out int result)
    {
        switch (value)
        {
            case int intValue:
                result = intValue;
                return true;
            case double doubleValue when
                doubleValue >= int.MinValue &&
                doubleValue <= int.MaxValue &&
                doubleValue == Math.Truncate(doubleValue):
                result = (int)doubleValue;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static string NormalizeChoice(string value, params ReadOnlySpan<string> choices)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        foreach (string choice in choices)
        {
            if (string.Equals(value, choice, StringComparison.Ordinal))
            {
                return choice;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(value), value, "The setting value is not supported.");
    }

    private static string NormalizeDelayTestUrl(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string trimmed = value.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException(
                "The latency test URL must be an absolute HTTP or HTTPS URL without user information.",
                nameof(value));
        }

        return uri.AbsoluteUri;
    }

    private static string NormalizeStoredDelayTestUrl(string value)
    {
        try
        {
            return NormalizeDelayTestUrl(value);
        }
        catch (ArgumentException)
        {
            return DefaultDelayTestUrl;
        }
    }

    private void SetAndPersist<T>(
        ref T field,
        T value,
        string key,
        [CallerMemberName] string? propertyName = null)
        where T : notnull
    {
        if (SetProperty(ref field, value, propertyName))
        {
            Persist(key, value);
        }
    }

    private T Read<T>(string key, T fallback)
        where T : notnull
    {
        if (_storage?.Values.TryGetValue(key, out object? value) == true && value is T typed)
        {
            return typed;
        }

        return fallback;
    }

    private void Persist<T>(string key, T value)
        where T : notnull
    {
        if (_storage is null)
        {
            return;
        }

        try
        {
            _storage.Values[key] = value;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine($"App setting '{key}' could not be persisted: {exception.Message}");
        }
    }

    private static ApplicationDataContainer? TryGetStorage()
    {
        try
        {
            return ApplicationData.Current.LocalSettings;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
