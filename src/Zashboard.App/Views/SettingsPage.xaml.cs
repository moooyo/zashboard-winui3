using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using Zashboard.App.Controls;
using Zashboard.App.ViewModels;

namespace Zashboard.App.Views;

public sealed partial class SettingsPage : Page
{
    private const string ConfigurationPathHelpText =
        "Enter a path accessible to the controller, or leave it empty when supplying a YAML payload.";
    private const string ConfigurationPayloadHelpText =
        "Paste the complete configuration YAML, or use the load local file command.";
    private const string DnsNameHelpText =
        "Enter the domain name to query through the active controller.";
    private const string DnsTypeHelpText =
        "Enter a DNS record type such as A, AAAA, HTTPS, SRV, or TXT.";
    private const string DelayTestUrlHelpText =
        "Enter an absolute HTTP or HTTPS URL used for proxy latency measurements.";

    private bool _isInitializing = true;
    private bool _isApplyingSettings;
    private bool _isApplyingControllerState;
    private bool _canPatchConfiguration;
    private string _listenerSettingsAvailabilityMessage = string.Empty;

    public SettingsPage()
    {
        InitializeComponent();
        _isInitializing = false;
    }

    public event EventHandler? ManageBackendsRequested;

    public event EventHandler? ClearCapabilityCacheRequested;

    public event EventHandler? ReloadConfigurationRequested;

    public event EventHandler<ConfigurationUpdateRequestedEventArgs>? UpdateConfigurationRequested;

    public event EventHandler<NetworkConfigurationRequestedEventArgs>? NetworkConfigurationRequested;

    public event EventHandler<DnsQueryRequestedEventArgs>? QueryDnsRequested;

    public event EventHandler<DnsAnswerCopyRequestedEventArgs>? DnsAnswerCopyRequested;

    public event EventHandler? FlushDnsCacheRequested;

    public event EventHandler? FlushFakeIpCacheRequested;

    public event EventHandler? UpdateGeoDataRequested;

    public event EventHandler? RestartCoreRequested;

    public event EventHandler<CoreUpgradeRequestedEventArgs>? UpgradeCoreRequested;

    public event EventHandler<SettingChangedEventArgs>? SettingChanged;

    public ObservableCollection<DnsAnswerDisplayItem> DnsAnswers { get; } =
        new BulkObservableCollection<DnsAnswerDisplayItem>();

    public object? ViewModel
    {
        get => DataContext;
        set => DataContext = value;
    }

    public void SetAppVersion(string version)
    {
        AppVersionText.Text = version;
        AutomationProperties.SetName(AppVersionText, $"Zashboard version {version}");
    }

    public void ApplySettings(SettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        _isApplyingSettings = true;
        try
        {
            StartWithWindowsToggle.IsOn = viewModel.StartWithWindows;
            SelectComboBoxValue(CloseBehaviorComboBox, viewModel.CloseBehavior);
            SelectComboBoxValue(ThemeComboBox, viewModel.Theme);
            MicaBackdropToggle.IsOn = viewModel.MicaBackdrop;
            LogBufferSizeNumberBox.Value = viewModel.LogBufferSize;
            DelayTestUrlTextBox.Text = viewModel.DelayTestUrl;
            DelayTimeoutNumberBox.Value = viewModel.DelayTimeoutSeconds;
        }
        finally
        {
            _isApplyingSettings = false;
        }
    }

    public void ApplySetting(SettingsViewModel viewModel, string propertyName)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);

        _isApplyingSettings = true;
        try
        {
            switch (propertyName)
            {
                case nameof(SettingsViewModel.StartWithWindows):
                    StartWithWindowsToggle.IsOn = viewModel.StartWithWindows;
                    break;
                case nameof(SettingsViewModel.CloseBehavior):
                    SelectComboBoxValue(CloseBehaviorComboBox, viewModel.CloseBehavior);
                    break;
                case nameof(SettingsViewModel.Theme):
                    SelectComboBoxValue(ThemeComboBox, viewModel.Theme);
                    break;
                case nameof(SettingsViewModel.MicaBackdrop):
                    MicaBackdropToggle.IsOn = viewModel.MicaBackdrop;
                    break;
                case nameof(SettingsViewModel.LogBufferSize):
                    LogBufferSizeNumberBox.Value = viewModel.LogBufferSize;
                    break;
                case nameof(SettingsViewModel.DelayTestUrl):
                    DelayTestUrlTextBox.Text = viewModel.DelayTestUrl;
                    AutomationProperties.SetIsDataValidForForm(DelayTestUrlTextBox, true);
                    AutomationProperties.SetHelpText(DelayTestUrlTextBox, DelayTestUrlHelpText);
                    SetValidationMessage(DelayTestUrlValidationText, null);
                    break;
                case nameof(SettingsViewModel.DelayTimeoutSeconds):
                    DelayTimeoutNumberBox.Value = viewModel.DelayTimeoutSeconds;
                    break;
            }
        }
        finally
        {
            _isApplyingSettings = false;
        }
    }

    public void ApplyControllerState(SettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ApplyControllerAvailability(viewModel);
        ApplyListenerSettings(viewModel);
        ApplyDnsStatus(viewModel);
    }

    public void ApplyControllerAvailability(SettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _canPatchConfiguration = viewModel.CanPatchConfiguration;
        _listenerSettingsAvailabilityMessage = viewModel.ListenerSettingsStatus;
        StartWithWindowsToggle.IsEnabled = viewModel.CanStartUserOperation;
        ApplyListenerSettingsButton.IsEnabled = viewModel.CanPatchConfiguration && viewModel.CanStartUserOperation;
        HttpPortNumberBox.IsEnabled = viewModel.CanPatchConfiguration;
        SocksPortNumberBox.IsEnabled = viewModel.CanPatchConfiguration;
        RedirPortNumberBox.IsEnabled = viewModel.CanPatchConfiguration;
        TProxyPortNumberBox.IsEnabled = viewModel.CanPatchConfiguration;
        MixedPortNumberBox.IsEnabled = viewModel.CanPatchConfiguration;
        AllowLanToggle.IsEnabled = viewModel.CanPatchConfiguration;
        ReloadConfigurationButton.IsEnabled = viewModel.CanReloadConfiguration && viewModel.CanStartUserOperation;
        UpdateConfigurationButton.IsEnabled = viewModel.CanUpdateConfiguration && viewModel.CanStartUserOperation;
        QueryDnsButton.IsEnabled = viewModel.HasControlSession && viewModel.CanStartUserOperation;
        FlushDnsCacheButton.IsEnabled = viewModel.HasControlSession && viewModel.CanStartUserOperation;
        FlushFakeIpCacheButton.IsEnabled = viewModel.HasControlSession && viewModel.CanStartUserOperation;
        UpdateGeoDataButton.IsEnabled = viewModel.CanUpdateGeoData && viewModel.CanStartUserOperation;
        RestartCoreButton.IsEnabled = viewModel.CanRestartCore && viewModel.CanStartUserOperation;
        UpgradeCoreButton.IsEnabled = viewModel.CanUpgradeCore && viewModel.CanStartUserOperation;
        ReprobeCapabilitiesButton.IsEnabled = viewModel.HasControlSession && viewModel.CanStartUserOperation;
        ControllerConfigurationStatusText.Text = viewModel.ControllerConfigurationStatus;
        AutomationProperties.SetName(
            ControllerConfigurationStatusText,
            $"Controller configuration status. {viewModel.ControllerConfigurationStatus}");
        AutomationProperties.SetHelpText(
            ListenerSettingsPanel,
            viewModel.ListenerSettingsStatus);
        AutomationProperties.SetHelpText(
            ReloadConfigurationButton,
            viewModel.CanReloadConfiguration
                ? "Reload the active controller configuration source."
                : viewModel.ControllerConfigurationStatus);
        AutomationProperties.SetHelpText(
            UpdateConfigurationButton,
            viewModel.CanUpdateConfiguration
                ? "Validate and apply the supplied controller path or payload."
                : viewModel.ControllerConfigurationStatus);
        UpdateListenerSettingsStatus();
    }

    public void ApplyListenerSettings(SettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ListenerSettingsDraft settings = viewModel.GetListenerSettingsForDisplay();

        _isApplyingControllerState = true;
        try
        {
            HttpPortNumberBox.Value = settings.HttpPort;
            SocksPortNumberBox.Value = settings.SocksPort;
            RedirPortNumberBox.Value = settings.RedirPort;
            TProxyPortNumberBox.Value = settings.TProxyPort;
            MixedPortNumberBox.Value = settings.MixedPort;
            AllowLanToggle.IsOn = settings.AllowLan;
        }
        finally
        {
            _isApplyingControllerState = false;
        }

        NumberBox[] inputs =
        [
            HttpPortNumberBox,
            SocksPortNumberBox,
            RedirPortNumberBox,
            TProxyPortNumberBox,
            MixedPortNumberBox,
        ];
        bool hasInvalidInput = false;
        foreach (NumberBox input in inputs)
        {
            bool isValid = SettingsFormPolicy.IsValidListenerPort(input.Value);
            hasInvalidInput |= !isValid;
            AutomationProperties.SetIsDataValidForForm(input, isValid);
            AutomationProperties.SetHelpText(
                input,
                isValid
                    ? "Enter a whole-number listener port between 0 and 65535."
                    : SettingsFormPolicy.ListenerPortValidationMessage);
        }

        SetValidationMessage(
            ListenerValidationText,
            hasInvalidInput ? SettingsFormPolicy.ListenerPortValidationMessage : null);
        UpdateListenerSettingsStatus();
    }

    public void MarkListenerSettingsSaved(SettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        viewModel.MarkListenerSettingsSaved();
        ApplyListenerSettings(viewModel);
    }

    public void ApplyDnsStatus(SettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DnsSummaryText.Text = viewModel.DnsSummary;
        AutomationProperties.SetName(DnsSummaryText, $"DNS query status. {viewModel.DnsSummary}");
    }

    public void ShowDnsCopyConfirmation()
    {
        DnsCopyStatusText.Text = "DNS answer value copied to the clipboard.";
        DnsCopyStatusText.Visibility = Visibility.Visible;
        AutomationProperties.SetName(DnsCopyStatusText, DnsCopyStatusText.Text);
    }

    public void ShowMessage(string title, string message, InfoBarSeverity severity)
    {
        SettingsInfoBar.Title = title;
        SettingsInfoBar.Message = message;
        SettingsInfoBar.Severity = severity;
        AutomationProperties.SetItemStatus(SettingsInfoBar, severity.ToString());
        AutomationProperties.SetName(SettingsInfoBar, $"{title}. {message}");
        SettingsInfoBar.IsOpen = true;
    }

    public void ClearErrorMessage()
    {
        if (SettingsInfoBar.Severity == InfoBarSeverity.Error &&
            string.Equals(SettingsInfoBar.Title, "Operation failed", StringComparison.Ordinal))
        {
            SettingsInfoBar.IsOpen = false;
        }
    }

    private void OnManageBackendsClicked(object sender, RoutedEventArgs args)
    {
        ManageBackendsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnClearCapabilityCacheClicked(object sender, RoutedEventArgs args)
    {
        ClearCapabilityCacheRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void OnReloadConfigurationClicked(object sender, RoutedEventArgs args)
    {
        if (await ConfirmAsync(
            "Reload controller configuration?",
            "The controller will reload its current source. Active routing behavior may change immediately.",
            "Reload"))
        {
            ReloadConfigurationRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private async void OnApplyListenerSettingsClicked(object sender, RoutedEventArgs args)
    {
        NumberBox[] inputs =
        [
            HttpPortNumberBox,
            SocksPortNumberBox,
            RedirPortNumberBox,
            TProxyPortNumberBox,
            MixedPortNumberBox,
        ];
        NumberBox? invalid = inputs.FirstOrDefault(static input =>
            !SettingsFormPolicy.IsValidListenerPort(input.Value));
        if (invalid is not null)
        {
            const string message = SettingsFormPolicy.ListenerPortValidationMessage;
            AutomationProperties.SetIsDataValidForForm(invalid, false);
            AutomationProperties.SetHelpText(invalid, message);
            SetValidationMessage(ListenerValidationText, message);
            ShowMessage("Invalid listener port", message, InfoBarSeverity.Warning);
            invalid.Focus(FocusState.Keyboard);
            return;
        }

        foreach (NumberBox input in inputs)
        {
            AutomationProperties.SetIsDataValidForForm(input, true);
            AutomationProperties.SetHelpText(
                input,
                "Enter a whole-number listener port between 0 and 65535.");
        }

        SetValidationMessage(ListenerValidationText, null);

        if (!await ConfirmAsync(
            "Apply listener settings?",
            "Changing controller listeners may interrupt clients that use these ports.",
            "Apply"))
        {
            return;
        }

        NetworkConfigurationRequested?.Invoke(
            this,
            new NetworkConfigurationRequestedEventArgs(
                checked((int)HttpPortNumberBox.Value),
                checked((int)SocksPortNumberBox.Value),
                checked((int)RedirPortNumberBox.Value),
                checked((int)TProxyPortNumberBox.Value),
                checked((int)MixedPortNumberBox.Value),
                AllowLanToggle.IsOn));
    }

    private void OnListenerPortChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isInitializing || _isApplyingControllerState)
        {
            return;
        }

        PersistListenerSettingsDraft();
        AutomationProperties.SetIsDataValidForForm(sender, true);
        AutomationProperties.SetHelpText(
            sender,
            "Enter a whole-number listener port between 0 and 65535.");
        SetValidationMessage(ListenerValidationText, null);
        UpdateListenerSettingsStatus();
    }

    private void OnAllowLanToggled(object sender, RoutedEventArgs args)
    {
        if (_isInitializing || _isApplyingControllerState)
        {
            return;
        }

        PersistListenerSettingsDraft();
        UpdateListenerSettingsStatus();
    }

    private async void OnLoadConfigurationFileClicked(object sender, RoutedEventArgs args)
    {
        try
        {
            FileOpenPicker picker = new()
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeFilter.Add(".yaml");
            picker.FileTypeFilter.Add(".yml");
            picker.FileTypeFilter.Add(".json");
            picker.FileTypeFilter.Add(".conf");

            MainWindow? mainWindow = (Application.Current as App)?.MainWindow;
            if (mainWindow is null)
            {
                throw new InvalidOperationException("The application window is not available.");
            }

            nint windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(mainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
            StorageFile? file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            ConfigurationPayloadTextBox.Text = await FileIO.ReadTextAsync(file);
            ConfigurationPathTextBox.Text = string.Empty;
            ShowMessage("Configuration loaded", file.Name, InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowMessage("File could not be loaded", exception.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnUpdateConfigurationClicked(object sender, RoutedEventArgs args)
    {
        string path = ConfigurationPathTextBox.Text.Trim();
        string payload = ConfigurationPayloadTextBox.Text;
        if (path.Length == 0 && string.IsNullOrWhiteSpace(payload))
        {
            const string validationMessage =
                "Enter a controller path or load a local configuration payload.";
            AutomationProperties.SetIsDataValidForForm(ConfigurationPathTextBox, false);
            AutomationProperties.SetIsDataValidForForm(ConfigurationPayloadTextBox, false);
            AutomationProperties.SetHelpText(ConfigurationPathTextBox, validationMessage);
            AutomationProperties.SetHelpText(ConfigurationPayloadTextBox, validationMessage);
            SetValidationMessage(ConfigurationValidationText, validationMessage);
            ShowMessage(
                "Configuration required",
                validationMessage,
                InfoBarSeverity.Warning);
            ConfigurationPathTextBox.Focus(FocusState.Keyboard);
            return;
        }

        AutomationProperties.SetIsDataValidForForm(ConfigurationPathTextBox, true);
        AutomationProperties.SetIsDataValidForForm(ConfigurationPayloadTextBox, true);
        AutomationProperties.SetHelpText(ConfigurationPathTextBox, ConfigurationPathHelpText);
        AutomationProperties.SetHelpText(
            ConfigurationPayloadTextBox,
            ConfigurationPayloadHelpText);
        SetValidationMessage(ConfigurationValidationText, null);

        if (await ConfirmAsync(
            "Apply controller configuration?",
            ForceConfigurationToggle.IsOn
                ? "The controller will force this update and may interrupt active connections."
                : "The controller will validate and apply this update. Active routing behavior may change.",
            "Apply"))
        {
            UpdateConfigurationRequested?.Invoke(
                this,
                new ConfigurationUpdateRequestedEventArgs(
                    path,
                    payload,
                    ForceConfigurationToggle.IsOn));
        }
    }

    private void OnConfigurationInputChanged(object sender, TextChangedEventArgs args)
    {
        if (_isInitializing)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(ConfigurationPathTextBox.Text) ||
            !string.IsNullOrWhiteSpace(ConfigurationPayloadTextBox.Text))
        {
            AutomationProperties.SetIsDataValidForForm(ConfigurationPathTextBox, true);
            AutomationProperties.SetIsDataValidForForm(ConfigurationPayloadTextBox, true);
            AutomationProperties.SetHelpText(ConfigurationPathTextBox, ConfigurationPathHelpText);
            AutomationProperties.SetHelpText(
                ConfigurationPayloadTextBox,
                ConfigurationPayloadHelpText);
            SetValidationMessage(ConfigurationValidationText, null);
        }
    }

    private void OnQueryDnsClicked(object sender, RoutedEventArgs args)
    {
        DnsCopyStatusText.Visibility = Visibility.Collapsed;
        string name = DnsNameTextBox.Text.Trim();
        if (name.Length == 0)
        {
            const string validationMessage = "Enter a domain name before running a DNS query.";
            AutomationProperties.SetIsDataValidForForm(DnsNameTextBox, false);
            AutomationProperties.SetHelpText(DnsNameTextBox, validationMessage);
            SetValidationMessage(DnsValidationText, validationMessage);
            ShowMessage("DNS name required", validationMessage, InfoBarSeverity.Warning);
            DnsNameTextBox.Focus(FocusState.Keyboard);
            return;
        }

        AutomationProperties.SetIsDataValidForForm(DnsNameTextBox, true);
        AutomationProperties.SetHelpText(DnsNameTextBox, DnsNameHelpText);
        string type = DnsTypeTextBox.Text.Trim();
        if (type.Length == 0)
        {
            const string validationMessage = "Enter a DNS record type before running a query.";
            AutomationProperties.SetIsDataValidForForm(DnsTypeTextBox, false);
            AutomationProperties.SetHelpText(DnsTypeTextBox, validationMessage);
            SetValidationMessage(DnsValidationText, validationMessage);
            ShowMessage("DNS type required", validationMessage, InfoBarSeverity.Warning);
            DnsTypeTextBox.Focus(FocusState.Keyboard);
            return;
        }

        AutomationProperties.SetIsDataValidForForm(DnsTypeTextBox, true);
        AutomationProperties.SetHelpText(DnsTypeTextBox, DnsTypeHelpText);
        SetValidationMessage(DnsValidationText, null);
        QueryDnsRequested?.Invoke(
            this,
            new DnsQueryRequestedEventArgs(name, type));
    }

    private void OnDnsNameChanged(object sender, TextChangedEventArgs args)
    {
        if (_isInitializing)
        {
            return;
        }

        if (sender is not TextBox textBox)
        {
            return;
        }

        AutomationProperties.SetIsDataValidForForm(textBox, true);
        AutomationProperties.SetHelpText(textBox, DnsNameHelpText);
        ClearDnsValidationWhenReady();
    }

    private void OnDnsTypeChanged(object sender, TextChangedEventArgs args)
    {
        if (_isInitializing)
        {
            return;
        }

        AutomationProperties.SetIsDataValidForForm(DnsTypeTextBox, true);
        AutomationProperties.SetHelpText(DnsTypeTextBox, DnsTypeHelpText);
        ClearDnsValidationWhenReady();
    }

    private void OnCopyDnsAnswerClicked(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: string value })
        {
            DnsAnswerCopyRequested?.Invoke(
                this,
                new DnsAnswerCopyRequestedEventArgs(value));
        }
    }

    private async void OnFlushDnsCacheClicked(object sender, RoutedEventArgs args)
    {
        if (await ConfirmAsync(
            "Flush DNS cache?",
            "Cached DNS records will be discarded from the active controller.",
            "Flush"))
        {
            FlushDnsCacheRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private async void OnFlushFakeIpCacheClicked(object sender, RoutedEventArgs args)
    {
        if (await ConfirmAsync(
            "Flush FakeIP cache?",
            "Existing FakeIP mappings will be discarded and may be assigned again.",
            "Flush"))
        {
            FlushFakeIpCacheRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnUpdateGeoDataClicked(object sender, RoutedEventArgs args)
    {
        UpdateGeoDataRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void OnRestartCoreClicked(object sender, RoutedEventArgs args)
    {
        if (await ConfirmAsync(
            "Restart the active core?",
            "Controller access and active connections may be interrupted while the core restarts.",
            "Restart"))
        {
            RestartCoreRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private async void OnUpgradeCoreClicked(object sender, RoutedEventArgs args)
    {
        string channel = UpgradeChannelComboBox.SelectedItem is ComboBoxItem { Tag: string value }
            ? value
            : "auto";
        if (await ConfirmAsync(
            "Upgrade the active core?",
            $"The controller will request the {channel} channel and may restart during installation.",
            "Upgrade"))
        {
            UpgradeCoreRequested?.Invoke(this, new CoreUpgradeRequestedEventArgs(channel));
        }
    }

    private void OnBooleanSettingToggled(object sender, RoutedEventArgs args)
    {
        if (_isInitializing || _isApplyingSettings)
        {
            return;
        }

        if (sender is ToggleSwitch toggle && toggle.Tag is string key)
        {
            SettingChanged?.Invoke(this, new SettingChangedEventArgs(key, toggle.IsOn));
        }
    }

    private void OnNumberSettingChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isInitializing || _isApplyingSettings)
        {
            return;
        }

        if (sender.Tag is not string key || double.IsNaN(args.NewValue))
        {
            return;
        }

        bool isLogBuffer = ReferenceEquals(sender, LogBufferSizeNumberBox);
        bool isValid = args.NewValue == Math.Truncate(args.NewValue) &&
            (isLogBuffer
                ? args.NewValue is >= 500 and <= 50_000
                : args.NewValue is >= 1 and <= 60);
        TextBlock validationText = isLogBuffer
            ? LogBufferSizeValidationText
            : DelayTimeoutValidationText;
        string helpText = isLogBuffer
            ? "Set a whole-number log buffer size from 500 to 50000."
            : "Set a whole-number timeout from 1 to 60 seconds.";
        if (!isValid)
        {
            AutomationProperties.SetIsDataValidForForm(sender, false);
            AutomationProperties.SetHelpText(sender, helpText);
            SetValidationMessage(validationText, helpText);
            double authoritativeValue = ViewModel is SettingsViewModel viewModel
                ? isLogBuffer ? viewModel.LogBufferSize : viewModel.DelayTimeoutSeconds
                : args.OldValue;
            _isApplyingSettings = true;
            try
            {
                sender.Value = authoritativeValue;
            }
            finally
            {
                _isApplyingSettings = false;
            }

            sender.Focus(FocusState.Keyboard);
            return;
        }

        AutomationProperties.SetIsDataValidForForm(sender, true);
        AutomationProperties.SetHelpText(sender, helpText);
        SetValidationMessage(validationText, null);
        SettingChanged?.Invoke(this, new SettingChangedEventArgs(key, args.NewValue));
    }

    private void OnDelayTestUrlLostFocus(object sender, RoutedEventArgs args)
    {
        if (_isApplyingSettings)
        {
            return;
        }

        string value = DelayTestUrlTextBox.Text.Trim();
        if (!SettingsFormPolicy.IsValidDelayTestUrl(value))
        {
            const string validationMessage = SettingsFormPolicy.DelayTestUrlValidationMessage;
            AutomationProperties.SetIsDataValidForForm(DelayTestUrlTextBox, false);
            AutomationProperties.SetHelpText(DelayTestUrlTextBox, validationMessage);
            SetValidationMessage(DelayTestUrlValidationText, validationMessage);
            return;
        }

        AutomationProperties.SetIsDataValidForForm(DelayTestUrlTextBox, true);
        AutomationProperties.SetHelpText(DelayTestUrlTextBox, DelayTestUrlHelpText);
        SetValidationMessage(DelayTestUrlValidationText, null);
        SettingChanged?.Invoke(
            this,
            new SettingChangedEventArgs("delayTestUrl", value));
    }

    private void OnDelayTestUrlChanged(object sender, TextChangedEventArgs args)
    {
        if (_isInitializing || _isApplyingSettings)
        {
            return;
        }

        AutomationProperties.SetIsDataValidForForm(DelayTestUrlTextBox, true);
        AutomationProperties.SetHelpText(DelayTestUrlTextBox, DelayTestUrlHelpText);
        SetValidationMessage(DelayTestUrlValidationText, null);
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs args)
    {
        RaiseComboBoxSetting(sender, "theme");
    }

    private void OnCloseBehaviorChanged(object sender, SelectionChangedEventArgs args)
    {
        RaiseComboBoxSetting(sender, "closeBehavior");
    }

    private void RaiseComboBoxSetting(object sender, string key)
    {
        if (_isInitializing || _isApplyingSettings)
        {
            return;
        }

        if (sender is ComboBox comboBox &&
            comboBox.SelectedItem is ComboBoxItem item &&
            item.Tag is string value)
        {
            SettingChanged?.Invoke(this, new SettingChangedEventArgs(key, value));
        }
    }

    private async Task<bool> ConfirmAsync(string title, string content, string primaryButtonText)
    {
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static void SelectComboBoxValue(ComboBox comboBox, string value)
    {
        foreach (object item in comboBox.Items)
        {
            if (item is ComboBoxItem option && string.Equals(
                option.Tag as string,
                value,
                StringComparison.Ordinal))
            {
                comboBox.SelectedItem = option;
                return;
            }
        }
    }

    private void ClearDnsValidationWhenReady()
    {
        if (!string.IsNullOrWhiteSpace(DnsNameTextBox.Text) &&
            !string.IsNullOrWhiteSpace(DnsTypeTextBox.Text))
        {
            SetValidationMessage(DnsValidationText, null);
        }
    }

    private void UpdateListenerSettingsStatus()
    {
        bool hasDraft = ViewModel is SettingsViewModel viewModel &&
            viewModel.HasListenerSettingsDraft;
        string status = _canPatchConfiguration && hasDraft
            ? "Listener settings have unsaved changes. Review them, then select Apply listener settings."
            : _listenerSettingsAvailabilityMessage;
        ListenerSettingsStatusText.Text = status;
        AutomationProperties.SetName(
            ListenerSettingsStatusText,
            $"Listener settings status. {status}");
        AutomationProperties.SetItemStatus(ListenerSettingsPanel, status);
    }

    private void PersistListenerSettingsDraft()
    {
        if (ViewModel is not SettingsViewModel viewModel)
        {
            return;
        }

        viewModel.UpdateListenerSettingsDraft(
            new ListenerSettingsDraft(
                HttpPortNumberBox.Value,
                SocksPortNumberBox.Value,
                RedirPortNumberBox.Value,
                TProxyPortNumberBox.Value,
                MixedPortNumberBox.Value,
                AllowLanToggle.IsOn));
    }

    private static void SetValidationMessage(TextBlock target, string? message)
    {
        target.Text = message ?? string.Empty;
        target.Visibility = string.IsNullOrEmpty(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
        AutomationProperties.SetName(target, message ?? string.Empty);
    }
}

public sealed class ConfigurationUpdateRequestedEventArgs(
    string path,
    string payload,
    bool force) : EventArgs
{
    public string Path { get; } = path;

    public string Payload { get; } = payload;

    public bool Force { get; } = force;
}

public sealed class NetworkConfigurationRequestedEventArgs(
    int httpPort,
    int socksPort,
    int redirPort,
    int tProxyPort,
    int mixedPort,
    bool allowLan) : EventArgs
{
    public int HttpPort { get; } = httpPort;

    public int SocksPort { get; } = socksPort;

    public int RedirPort { get; } = redirPort;

    public int TProxyPort { get; } = tProxyPort;

    public int MixedPort { get; } = mixedPort;

    public bool AllowLan { get; } = allowLan;
}

public sealed class DnsQueryRequestedEventArgs(string name, string type) : EventArgs
{
    public string Name { get; } = name;

    public string Type { get; } = type;
}

public sealed class DnsAnswerCopyRequestedEventArgs(string value) : EventArgs
{
    public string Value { get; } = value;
}

public sealed class CoreUpgradeRequestedEventArgs(string channel) : EventArgs
{
    public string Channel { get; } = channel;
}

public sealed class SettingChangedEventArgs(string key, object value) : EventArgs
{
    public string Key { get; } = key;

    public object Value { get; } = value;
}
