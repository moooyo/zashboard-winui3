using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Zashboard.App.Controls;
using Zashboard.App.Services;
using Zashboard.Core.Backends;

namespace Zashboard.App.Views;

public sealed partial class BackendSetupPage : Page
{
    private const double CompactPaneThreshold = 760;

    private const string ControllerAddressHelpText =
        "Enter an absolute HTTP or HTTPS controller address.";

    private EditorBaseline _baseline = EditorBaseline.Empty;
    private bool _addressHasBeenEdited;
    private bool _hadSelectionBeforeReset;
    private bool _isChangingSelection;
    private bool _isReplacingBackends;
    private bool _isShowingAddressError;
    private bool _isUpdatingEditor;
    private string? _pendingRemovalBackendId;
    private int _pendingRemovalIndex = -1;
    private string? _selectedBackendId;

    public BackendSetupPage()
    {
        InitializeComponent();
        SavedBackends.CollectionChanged += (_, _) => UpdateEmptyState();
        SavedBackends.Resetting += OnBackendsResetting;
        SavedBackends.ResetCompleted += OnBackendsResetCompleted;
        ClearEditor(focusAddress: false);
        UpdateEmptyState();
    }

    public event EventHandler<BackendConnectRequestedEventArgs>? ConnectRequested;

    public event EventHandler<BackendSelectedEventArgs>? RemoveRequested;

    public event EventHandler<BackendSelectedEventArgs>? ActivateRequested;

    public BulkObservableCollection<BackendDisplayItem> SavedBackends { get; } = [];

    public object? ViewModel
    {
        get => DataContext;
        set => DataContext = value;
    }

    public void ShowMessage(string title, string message, InfoBarSeverity severity)
    {
        _isShowingAddressError = false;
        BackendInfoBar.Title = title;
        BackendInfoBar.Message = message;
        BackendInfoBar.Severity = severity;
        AutomationProperties.SetItemStatus(BackendInfoBar, severity.ToString());
        AutomationProperties.SetName(BackendInfoBar, $"{title}. {message}");
        BackendInfoBar.IsOpen = true;
    }

    public void ClearErrorMessage()
    {
        if (BackendInfoBar.Severity == InfoBarSeverity.Error &&
            string.Equals(BackendInfoBar.Title, "Operation failed", StringComparison.Ordinal))
        {
            BackendInfoBar.IsOpen = false;
        }

        _isShowingAddressError = false;
    }

    public void HandleBackendSaved(string backendId)
    {
        BackendDisplayItem? backend = FindBackend(backendId);
        if (backend is null)
        {
            return;
        }

        ApplyBackendSelection(backend, focusAddress: false);
    }

    public void HandleBackendRemoved(string backendId)
    {
        if (!string.Equals(_pendingRemovalBackendId, backendId, StringComparison.Ordinal))
        {
            return;
        }

        int removedIndex = _pendingRemovalIndex;
        _pendingRemovalBackendId = null;
        _pendingRemovalIndex = -1;

        if (SavedBackends.Count == 0)
        {
            ApplyBackendSelection(null, focusAddress: false);
            _ = DispatcherQueue.TryEnqueue(() =>
                NewBackendButton.Focus(FocusState.Keyboard));
            return;
        }

        int nextIndex = Math.Clamp(removedIndex, 0, SavedBackends.Count - 1);
        BackendDisplayItem nextBackend = SavedBackends[nextIndex];
        ApplyBackendSelection(nextBackend, focusAddress: false);
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (SavedBackendsList.ContainerFromItem(nextBackend) is Control item)
            {
                item.Focus(FocusState.Keyboard);
            }
            else
            {
                SavedBackendsList.Focus(FocusState.Keyboard);
            }
        });
    }

    private void OnEditorInputChanged(object sender, RoutedEventArgs args)
    {
        if (_isUpdatingEditor)
        {
            return;
        }

        if (ReferenceEquals(sender, ControllerAddressBox))
        {
            _addressHasBeenEdited = true;
        }

        UpdateEditorState();
    }

    private void OnBackendLayoutSizeChanged(object sender, SizeChangedEventArgs args)
    {
        bool useCompactLayout = args.NewSize.Width < CompactPaneThreshold;
        SavedColumn.Width = useCompactLayout
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(320);
        GutterColumn.Width = useCompactLayout
            ? new GridLength(0)
            : new GridLength(16);
        EditorColumn.Width = useCompactLayout
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        SavedRow.Height = useCompactLayout
            ? new GridLength(300)
            : new GridLength(520);
        EditorGutterRow.Height = useCompactLayout
            ? new GridLength(16)
            : new GridLength(0);
        EditorRow.Height = useCompactLayout
            ? GridLength.Auto
            : new GridLength(0);
        Grid.SetRow(SavedPane, 0);
        Grid.SetColumn(SavedPane, 0);
        Grid.SetRow(EditorPane, useCompactLayout ? 2 : 0);
        Grid.SetColumn(EditorPane, useCompactLayout ? 0 : 2);
    }

    private void OnConnectClicked(object sender, RoutedEventArgs args)
    {
        if (!UpdateAddressValidation(
                showEmptyError: true,
                out BackendEndpoint? endpoint,
                out string? error))
        {
            ShowAddressError(error ?? ControllerAddressHelpText);
            ControllerAddressBox.Focus(FocusState.Keyboard);
            ControllerAddressBox.SelectAll();
            return;
        }

        if (!IsCredentialChoiceSafe(endpoint!))
        {
            const string message =
                "The controller address changed. Enter a replacement secret or choose to connect without one.";
            ShowMessage("Credential choice required", message, InfoBarSeverity.Warning);
            CredentialInfoBar.Message = message;
            CredentialInfoBar.Severity = InfoBarSeverity.Warning;
            ControllerSecretBox.Focus(FocusState.Keyboard);
            return;
        }

        BackendCredentialUpdate credentialUpdate = GetCredentialUpdate();
        string secret = credentialUpdate == BackendCredentialUpdate.Replace
            ? ControllerSecretBox.Password
            : string.Empty;
        ConnectRequested?.Invoke(
            this,
            new BackendConnectRequestedEventArgs(
                BackendNameBox.Text.Trim(),
                endpoint!.BaseUri,
                secret,
                _selectedBackendId,
                credentialUpdate));
    }

    private async void OnNewBackendClicked(object sender, RoutedEventArgs args)
    {
        if (!await ConfirmDiscardChangesAsync(
                "Create a new backend?",
                "Your unsaved changes to the current backend will be discarded."))
        {
            return;
        }

        ApplyBackendSelection(null, focusAddress: true);
    }

    private async void OnRemoveBackendClicked(object sender, RoutedEventArgs args)
    {
        if (SavedBackendsList.SelectedItem is not BackendDisplayItem backend)
        {
            return;
        }

        string consequence = backend.IsActive
            ? " This backend is active, so removing it will disconnect the app and clear current controller data."
            : string.Empty;
        string message =
            $"Name: {backend.Name}\nAddress: {backend.Address}\n\n" +
            "The saved profile and its encrypted credential will be permanently deleted." +
            consequence;
        if (!await ConfirmAsync(
                $"Remove backend '{backend.Name}'?",
                message,
                "Remove"))
        {
            return;
        }

        _pendingRemovalBackendId = backend.Id;
        _pendingRemovalIndex = SavedBackendsList.SelectedIndex;
        RemoveRequested?.Invoke(this, new BackendSelectedEventArgs(backend.Id));
    }

    private void OnActivateBackendClicked(object sender, RoutedEventArgs args)
    {
        if (SavedBackendsList.SelectedItem is BackendDisplayItem { IsActive: false } backend)
        {
            ActivateRequested?.Invoke(this, new BackendSelectedEventArgs(backend.Id));
        }
    }

    private async void OnSavedBackendSelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (_isReplacingBackends || _isChangingSelection)
        {
            return;
        }

        BackendDisplayItem? backend = SavedBackendsList.SelectedItem as BackendDisplayItem;
        if (string.Equals(backend?.Id, _selectedBackendId, StringComparison.Ordinal))
        {
            UpdateSelectionActions(backend);
            return;
        }

        string? requestedBackendId = backend?.Id;
        if (!await ConfirmDiscardChangesAsync(
                "Switch backends?",
                "Your unsaved changes to the current backend will be discarded."))
        {
            RestoreEditorSelection();
            return;
        }

        BackendDisplayItem? currentTarget = requestedBackendId is null
            ? null
            : FindBackend(requestedBackendId);
        if (requestedBackendId is not null && currentTarget is null)
        {
            RestoreEditorSelection();
            return;
        }

        ApplyBackendSelection(currentTarget, focusAddress: false);
    }

    private void OnClearSavedSecretChanged(object sender, RoutedEventArgs args)
    {
        if (_isUpdatingEditor)
        {
            return;
        }

        ControllerSecretBox.IsEnabled = ClearSavedSecretCheckBox.IsChecked != true;
        UpdateEditorState();
    }

    private void UpdateEmptyState()
    {
        bool isEmpty = SavedBackends.Count == 0;
        SavedBackendsList.Visibility = isEmpty
            ? Visibility.Collapsed
            : Visibility.Visible;
        SavedBackendsList.IsTabStop = !isEmpty;
        SavedBackendsEmptyState.Visibility = isEmpty
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (isEmpty && BackendInfoBar.Severity != InfoBarSeverity.Error)
        {
            ShowMessage(
                "Backend required",
                "Add a controller before using proxies, connections, rules, or live traffic.",
                InfoBarSeverity.Informational);
        }
        else if (!isEmpty && BackendInfoBar.Severity == InfoBarSeverity.Informational)
        {
            BackendInfoBar.IsOpen = false;
        }
    }

    private void OnBackendsResetting(object? sender, EventArgs args)
    {
        _isReplacingBackends = true;
        _hadSelectionBeforeReset = SavedBackendsList.SelectedItem is BackendDisplayItem ||
            _selectedBackendId is not null;
    }

    private void OnBackendsResetCompleted(object? sender, EventArgs args)
    {
        BackendDisplayItem? replacement = _selectedBackendId is null
            ? null
            : FindBackend(_selectedBackendId);
        SavedBackendsList.SelectedItem = replacement;
        _isReplacingBackends = false;
        if (replacement is null && _hadSelectionBeforeReset)
        {
            ClearEditor(focusAddress: false);
        }
        else if (replacement is not null)
        {
            UpdateEditorState();
        }

        _hadSelectionBeforeReset = false;
        UpdateSelectionActions(replacement);
        UpdateEmptyState();
    }

    private void ApplyBackendSelection(BackendDisplayItem? backend, bool focusAddress)
    {
        _isChangingSelection = true;
        try
        {
            SavedBackendsList.SelectedItem = backend;
        }
        finally
        {
            _isChangingSelection = false;
        }

        if (backend is null)
        {
            ClearEditor(focusAddress);
        }
        else
        {
            LoadEditor(backend);
        }
    }

    private void RestoreEditorSelection()
    {
        BackendDisplayItem? previous = _selectedBackendId is null
            ? null
            : FindBackend(_selectedBackendId);
        _isChangingSelection = true;
        try
        {
            SavedBackendsList.SelectedItem = previous;
        }
        finally
        {
            _isChangingSelection = false;
        }

        UpdateSelectionActions(previous);
    }

    private void LoadEditor(BackendDisplayItem backend)
    {
        _isUpdatingEditor = true;
        try
        {
            _selectedBackendId = backend.Id;
            BackendNameBox.Text = backend.Name;
            ControllerAddressBox.Text = backend.Address;
            ControllerSecretBox.Password = string.Empty;
            ClearSavedSecretCheckBox.IsChecked = false;
            ClearSavedSecretCheckBox.Visibility = Visibility.Visible;
            ControllerSecretBox.IsEnabled = true;
            _addressHasBeenEdited = false;
            _baseline = new EditorBaseline(
                backend.Id,
                backend.Name,
                backend.Address,
                string.Empty,
                RemoveSecret: false);
        }
        finally
        {
            _isUpdatingEditor = false;
        }

        UpdateSelectionActions(backend);
        UpdateEditorState();
    }

    private void ClearEditor(bool focusAddress)
    {
        _isUpdatingEditor = true;
        try
        {
            _selectedBackendId = null;
            BackendNameBox.Text = string.Empty;
            ControllerAddressBox.Text = string.Empty;
            ControllerSecretBox.Password = string.Empty;
            ClearSavedSecretCheckBox.IsChecked = false;
            ClearSavedSecretCheckBox.Visibility = Visibility.Collapsed;
            ControllerSecretBox.IsEnabled = true;
            _addressHasBeenEdited = false;
            _baseline = EditorBaseline.Empty;
        }
        finally
        {
            _isUpdatingEditor = false;
        }

        UpdateSelectionActions(null);
        UpdateEditorState();
        if (focusAddress)
        {
            ControllerAddressBox.Focus(FocusState.Keyboard);
        }
    }

    private void UpdateEditorState()
    {
        bool isValid = UpdateAddressValidation(
            showEmptyError: false,
            out BackendEndpoint? endpoint,
            out _);
        bool credentialChoiceIsSafe = endpoint is not null && IsCredentialChoiceSafe(endpoint);
        ConnectButton.IsEnabled = isValid && credentialChoiceIsSafe;
        UpdateCredentialState(endpoint);
        UpdateConnectButtonPresentation();
    }

    private bool UpdateAddressValidation(
        bool showEmptyError,
        out BackendEndpoint? endpoint,
        out string? error)
    {
        bool isValid = BackendEndpoint.TryCreate(ControllerAddressBox.Text, out endpoint, out error);
        bool showError = !isValid && (showEmptyError || _addressHasBeenEdited);
        AutomationProperties.SetIsDataValidForForm(ControllerAddressBox, isValid || !showError);
        AutomationProperties.SetHelpText(
            ControllerAddressBox,
            showError ? error ?? ControllerAddressHelpText : ControllerAddressHelpText);
        ControllerAddressErrorText.Text = showError ? error : string.Empty;
        ControllerAddressErrorText.Visibility = showError
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (isValid)
        {
            ClearAddressErrorMessage();
        }
        else if (showError && _isShowingAddressError)
        {
            ShowAddressError(error ?? ControllerAddressHelpText);
        }

        return isValid;
    }

    private void UpdateCredentialState(BackendEndpoint? endpoint)
    {
        BackendDisplayItem? selected = _selectedBackendId is null
            ? null
            : FindBackend(_selectedBackendId);
        bool isExisting = selected is not null;
        bool hasStoredCredential = selected?.HasStoredCredential == true;
        bool removeSecret = ClearSavedSecretCheckBox.IsChecked == true;
        bool hasReplacement = !string.IsNullOrEmpty(ControllerSecretBox.Password);
        bool endpointChanged = endpoint is not null && IsEndpointChanged(endpoint);

        ClearSavedSecretCheckBox.Visibility = hasStoredCredential
            ? Visibility.Visible
            : Visibility.Collapsed;
        string removeSecretLabel = endpointChanged
            ? "Connect to the new address without a secret"
            : "Remove the saved secret";
        ClearSavedSecretCheckBox.Content = removeSecretLabel;
        AutomationProperties.SetName(
            ClearSavedSecretCheckBox,
            removeSecretLabel);
        AutomationProperties.SetHelpText(
            ClearSavedSecretCheckBox,
            endpointChanged
                ? "Remove any encrypted credential before connecting to the new controller address."
                : "Remove the encrypted credential when this backend is saved.");
        ControllerSecretBox.Header = hasStoredCredential
            ? "Replacement secret"
            : "Secret (optional)";

        if (!hasStoredCredential)
        {
            CredentialInfoBar.Message = hasReplacement
                ? "The secret will be encrypted and saved for this backend."
                : isExisting
                    ? "No secret is saved for this backend."
                    : "No secret will be saved.";
            CredentialInfoBar.Severity = InfoBarSeverity.Informational;
            return;
        }

        if (removeSecret)
        {
            CredentialInfoBar.Message = endpointChanged
                ? "Any saved secret will be removed before connecting to the new address."
                : "Any saved secret will be removed when these changes are saved.";
            CredentialInfoBar.Severity = InfoBarSeverity.Warning;
        }
        else if (hasReplacement)
        {
            CredentialInfoBar.Message = endpointChanged
                ? "The replacement secret will be saved only for the new address."
                : "The replacement secret will overwrite the current saved value.";
            CredentialInfoBar.Severity = InfoBarSeverity.Informational;
        }
        else if (endpointChanged)
        {
            CredentialInfoBar.Message =
                "The address changed. Enter a replacement secret or choose to connect without one.";
            CredentialInfoBar.Severity = InfoBarSeverity.Warning;
        }
        else
        {
            CredentialInfoBar.Message =
                "A saved secret exists and will be kept unchanged. Saved secrets are never displayed here.";
            CredentialInfoBar.Severity = InfoBarSeverity.Informational;
        }
    }

    private void UpdateConnectButtonPresentation()
    {
        BackendDisplayItem? selected = _selectedBackendId is null
            ? null
            : FindBackend(_selectedBackendId);
        string label;
        string helpText;
        if (selected is null)
        {
            label = "Save and connect";
            helpText = "Save this backend and connect to it.";
        }
        else if (selected.IsActive)
        {
            label = HasUnsavedChanges ? "Save changes and reconnect" : "Reconnect";
            helpText = HasUnsavedChanges
                ? "Save these changes and reconnect to this backend."
                : "Reconnect to this backend.";
        }
        else
        {
            label = HasUnsavedChanges ? "Save changes and connect" : "Connect";
            helpText = HasUnsavedChanges
                ? "Save these changes and connect to this backend."
                : "Connect to this saved backend.";
        }

        ConnectButtonText.Text = label;
        AutomationProperties.SetName(ConnectButton, label);
        AutomationProperties.SetHelpText(ConnectButton, helpText);
        ToolTipService.SetToolTip(ConnectButton, helpText);
    }

    private void UpdateSelectionActions(BackendDisplayItem? backend)
    {
        RemoveBackendButton.IsEnabled = backend is not null;
        ActivateBackendButton.IsEnabled = backend is { IsActive: false };
    }

    private BackendCredentialUpdate GetCredentialUpdate()
    {
        if (ClearSavedSecretCheckBox.IsChecked == true)
        {
            return BackendCredentialUpdate.Remove;
        }

        if (!string.IsNullOrEmpty(ControllerSecretBox.Password))
        {
            return BackendCredentialUpdate.Replace;
        }

        BackendDisplayItem? selected = _selectedBackendId is null
            ? null
            : FindBackend(_selectedBackendId);
        return selected?.HasStoredCredential != true
            ? BackendCredentialUpdate.Remove
            : BackendCredentialUpdate.Keep;
    }

    private bool IsCredentialChoiceSafe(BackendEndpoint endpoint) =>
        !IsEndpointChanged(endpoint) || GetCredentialUpdate() != BackendCredentialUpdate.Keep;

    private bool IsEndpointChanged(BackendEndpoint endpoint)
    {
        if (_baseline.BackendId is null ||
            !BackendEndpoint.TryCreate(_baseline.Address, out BackendEndpoint? baseline, out _))
        {
            return false;
        }

        return !baseline!.Equals(endpoint);
    }

    private bool HasUnsavedChanges =>
        !string.Equals(BackendNameBox.Text, _baseline.Name, StringComparison.Ordinal) ||
        !string.Equals(ControllerAddressBox.Text, _baseline.Address, StringComparison.Ordinal) ||
        !string.Equals(ControllerSecretBox.Password, _baseline.Secret, StringComparison.Ordinal) ||
        (ClearSavedSecretCheckBox.IsChecked == true) != _baseline.RemoveSecret;

    private Task<bool> ConfirmDiscardChangesAsync(string title, string message) =>
        HasUnsavedChanges
            ? ConfirmAsync(title, message, "Discard changes")
            : Task.FromResult(true);

    private void ShowAddressError(string message)
    {
        ShowMessage("Invalid controller address", message, InfoBarSeverity.Error);
        _isShowingAddressError = true;
    }

    private void ClearAddressErrorMessage()
    {
        if (!_isShowingAddressError)
        {
            return;
        }

        BackendInfoBar.IsOpen = false;
        _isShowingAddressError = false;
    }

    private BackendDisplayItem? FindBackend(string backendId) =>
        SavedBackends.FirstOrDefault(backend => string.Equals(
            backend.Id,
            backendId,
            StringComparison.Ordinal));

    private async Task<bool> ConfirmAsync(string title, string message, string primaryText)
    {
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            PrimaryButtonText = primaryText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private readonly record struct EditorBaseline(
        string? BackendId,
        string Name,
        string Address,
        string Secret,
        bool RemoveSecret)
    {
        public static EditorBaseline Empty { get; } = new(
            null,
            string.Empty,
            string.Empty,
            string.Empty,
            RemoveSecret: false);
    }
}

public sealed class BackendConnectRequestedEventArgs(
    string name,
    Uri controllerUri,
    string secret,
    string? backendId,
    BackendCredentialUpdate credentialUpdate) : EventArgs
{
    public string Name { get; } = name;

    public Uri ControllerUri { get; } = controllerUri;

    public string Secret { get; } = secret;

    public string? BackendId { get; } = backendId;

    public BackendCredentialUpdate CredentialUpdate { get; } = credentialUpdate;
}

public sealed class BackendSelectedEventArgs(string backendId) : EventArgs
{
    public string BackendId { get; } = backendId;
}
