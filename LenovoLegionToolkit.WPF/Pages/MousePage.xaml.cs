using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Services.Mouse;
using LenovoLegionToolkit.Lib.Utils;
using LenovoLegionToolkit.WPF.Presentation;
using LenovoLegionToolkit.WPF.Utils;

namespace LenovoLegionToolkit.WPF.Pages;

public partial class MousePage
{
    private readonly MousePageSession _session;
    private readonly MouseDpiDraftEditor _dpiEditor;
    private readonly OptionalUiActivity _readActivity;
    private ConfirmedMouseValue<MouseDpi>? _displayedDpi;
    private ConfirmedMouseValue<MousePollingRate>? _displayedPolling;

    public MousePage()
    {
        var mouseService = IoCContainer.Resolve<MouseControlService>();
        _session = new MousePageSession(mouseService, CanChangeIndividual);
        _presetApplier = new MousePresetApplier(mouseService, mouseService, CanWrite);
        InitializeComponent();
        _dpiEditor = new MouseDpiDraftEditor(this, () => _session.CanEditDpi && !PresetBusy, RefreshControlAccess);
        _pollingSelector.ItemsSource = DeathAdderV3PollingProtocol.SupportedRates.Select(rate => new PollingChoice(rate)).ToArray();
        _session.Changed += Render;
        InitializeButtonUi(mouseService);
        _readActivity = new OptionalUiActivity(this, active =>
        {
            if (active) _ = ActivateQueriesAsync();
            else DeactivateQueries();
            return Task.CompletedTask;
        }, requireForeground: false, suspendForGaming: false);
    }

    private static bool CanWrite() => HardwareRuntime.ControlsEnabled && !AppFlags.Instance.ReadOnly &&
        !AppFlags.Instance.UiPreview && !Folders.IsReadOnlySession;

    private void MouseContent_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_pollingSurface is null || _assignmentEditor is null) return;
        ApplyMouseLayout(this, e.NewSize.Width);
    }

    // Pure presentation sizing; the detached-markup check exercises this exact
    // breakpoint without constructing a page or resolving a device service.
    internal static void ApplyMouseLayout(FrameworkElement scope, double width)
    {
        T Find<T>(string name) where T : class => (T)scope.FindName(name);
        var compact = width < 680;
        Find<System.Windows.Controls.ColumnDefinition>("_dpiColumn").Width = new GridLength(compact ? 1 : 3, GridUnitType.Star);
        Find<System.Windows.Controls.ColumnDefinition>("_pollingColumn").Width = compact ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
        var polling = Find<System.Windows.Controls.Border>("_pollingSurface");
        System.Windows.Controls.Grid.SetColumn(polling, compact ? 0 : 1);
        System.Windows.Controls.Grid.SetRow(polling, compact ? 1 : 0);
        Find<System.Windows.Controls.Border>("_dpiSurface").Margin = compact ? new Thickness(0, 0, 0, 12) : new Thickness(0, 0, 12, 0);
        // Give the illustration useful physical size, with the editor below it
        // on compact viewports. The Viewbox scales artwork and hit paths together.
        var stackMapping = compact;
        Find<System.Windows.Controls.ColumnDefinition>("_schematicColumn").Width = stackMapping
            ? new GridLength(1, GridUnitType.Star) : new GridLength(310);
        Find<System.Windows.Controls.ColumnDefinition>("_editorColumn").Width = stackMapping ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        var editor = Find<System.Windows.Controls.Border>("_assignmentEditor");
        System.Windows.Controls.Grid.SetColumn(editor, stackMapping ? 0 : 1);
        System.Windows.Controls.Grid.SetRow(editor, stackMapping ? 1 : 0);
        Find<System.Windows.Controls.StackPanel>("_schematicSurface").Margin = stackMapping ? new Thickness(0, 0, 0, 16) : new Thickness(0, 0, 18, 0);
        Find<System.Windows.Controls.Viewbox>("_schematicView").Height = compact ? 360 : 440;
    }
    private void Page_Loaded(object sender, RoutedEventArgs e) { }
    private async Task ActivateQueriesAsync()
    {
        var lifetime = ActivatePresets();
        await _presetApplyTask; // Drain a retired batch before reactivating reads.
        await _presetStorageTask; // A retired file read must finish before reloading this activation.
        if (!IsPresetCurrent(lifetime)) return;
        _buttons?.Activate();
        await _session.ActivateAsync();
        if (!IsPresetCurrent(lifetime)) return;
        if (_buttons is not null)
        {
            await _buttons.ReadProfilesAsync();
            if (!IsPresetCurrent(lifetime)) return;
            await _buttons.ReadAssignmentsAsync();
        }
        if (IsPresetCurrent(lifetime)) await LoadPresetsAsync();
    }
    private void Page_Unloaded(object sender, RoutedEventArgs e) => DeactivateQueries();
    private void DeactivateQueries()
    {
        DeactivatePresets();
        _session.Deactivate();
        _buttons?.Deactivate();
    }

    private void Render()
    {
        var dpi = _session.Dpi.Current;
        var polling = _session.Polling.Current;
        _currentDpiText.Text = dpi is not null ? $"{dpi.Value.X:N0} / {dpi.Value.Y:N0}" : "Unavailable";
        _pollingCurrentText.Text = polling is not null ? $"{(int)polling.Value:N0} Hz" : "Unavailable";
        _connectionText.Text = dpi is not null || polling is not null
            ? "Connected · verified device identity and HID interface"
            : _session.IsBusy ? "Reading device…" : "Current device state unavailable";
        if (dpi is not null && !ReferenceEquals(dpi, _displayedDpi))
        {
            _dpiEditor.SetConfirmed(dpi.Value);
        }
        if (polling is not null && !ReferenceEquals(polling, _displayedPolling))
            _pollingSelector.SelectedItem = _pollingSelector.Items.Cast<PollingChoice>().Single(choice => choice.Rate == polling.Value);
        else if (polling is null && !_session.IsBusy) _pollingSelector.SelectedIndex = -1;
        _displayedDpi = dpi;
        _displayedPolling = polling;
        _statusText.Text = _session.Dpi.Message;
        _pollingStatusText.Text = _session.Polling.Message;
        var lastDpi = _session.Dpi.LastConfirmed;
        var lastPolling = _session.Polling.LastConfirmed;
        _lastDpiText.Text = lastDpi is null ? "No confirmed DPI reading yet."
            : $"Last confirmed: {lastDpi.Value.X:N0} / {lastDpi.Value.Y:N0} DPI · {lastDpi.ReadAt:HH:mm:ss}";
        _lastPollingText.Text = lastPolling is null ? "No confirmed polling reading yet."
            : $"Last confirmed: {(int)lastPolling.Value:N0} Hz · {lastPolling.ReadAt:HH:mm:ss}";
        RefreshControlAccess();
    }

    public void RefreshControlAccess()
    {
        if (_applyButton is null || _session is null) return;
        var valid = TryDraft(out var requested);
        _refreshMouseButton.IsEnabled = !_session.IsBusy && _session.IsActive;
        _dpiX.IsEnabled = _dpiY.IsEnabled = _session.CanEditDpi;
        _resetButton.IsEnabled = _session.CanEditDpi;
        _applyButton.IsEnabled = _session.CanEditDpi && valid && requested != _session.Dpi.Current?.Value;
        if (_dpiValidationText is not null)
            _dpiValidationText.Text = _session.CanEditDpi && !valid ? "Enter 100–30,000 DPI in multiples of 50 for both axes." : "";
        if (_pollingApplyButton is not null)
        {
            _pollingSelector.IsEnabled = _session.CanEditPolling;
            _pollingApplyButton.IsEnabled = _session.CanEditPolling &&
                _pollingSelector.SelectedItem is PollingChoice choice && choice.Rate != _session.Polling.Current?.Value;
        }
        _permissionText.Text = CanWrite() ? "Hardware controls enabled for this session."
            : "Read-only: mouse settings are locked. Reading the device remains available.";
        RefreshButtonAccess();
        RefreshPresetAccess();
        if (PresetBusy)
        {
            _refreshMouseButton.IsEnabled = _dpiX.IsEnabled = _dpiY.IsEnabled = false;
            _resetButton.IsEnabled = _applyButton.IsEnabled = false;
            _pollingSelector.IsEnabled = false;
            if (_pollingApplyButton is not null) _pollingApplyButton.IsEnabled = false;
        }
        _dpiEditor?.UpdateAccess();
    }

    private bool TryDraft(out MouseDpi? dpi)
    {
        dpi = null;
        return _dpiEditor is not null && _dpiEditor.TryGet(out dpi);
    }
    private void PollingSelection_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => RefreshControlAccess();
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (!_session.CanEditDpi || PresetBusy || _session.Dpi.Current is not { } current) return;
        _dpiEditor.SetConfirmed(current.Value);
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (!PresetBusy) await _session.RefreshAsync();
    }

    private sealed record PollingChoice(MousePollingRate Rate)
    {
        public string Label => $"{(int)Rate:N0} Hz";
    }

    private async void ApplyPolling_Click(object sender, RoutedEventArgs e)
    {
        if (!_session.CanEditPolling || _session.Polling.Current is not { } previous ||
            _pollingSelector.SelectedItem is not PollingChoice choice || choice.Rate == previous.Value) return;
        var answer = System.Windows.MessageBox.Show(Window.GetWindow(this),
            $"Change polling configuration from {(int)previous.Value:N0} Hz to {(int)choice.Rate:N0} Hz?\n\n" +
            "The mouse is read before and after the change. DPI and saved stages are not edited. " +
            "Changes may persist on the mouse; reconnect persistence has not been tested. Higher rates can increase CPU use. " +
            "Writes are never retried automatically. An unclear result triggers one safe readback where possible.",
            "Confirm mouse polling change", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK || !_session.CanEditPolling) return;
        try { await HardwareCommandRunner.RunAsync(() => _session.ApplyPollingAsync(choice.Rate)); }
        catch (Exception ex) { _pollingStatusText.Text = "Failed · " + ex.Message; }
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!_session.CanEditDpi || _session.Dpi.Current is not { } previous ||
            !TryDraft(out var requested) || requested is null || requested == previous.Value) return;
        var answer = System.Windows.MessageBox.Show(Window.GetWindow(this),
            $"Change active DPI from {previous.Value.X} / {previous.Value.Y} to {requested.X} / {requested.Y}?\n\n" +
            "Only the active X/Y override is changed; saved stages are not edited. " +
            "The mouse is read before and after the change. Writes are never retried automatically. " +
            "An unclear result triggers one safe readback where possible.\n\n" +
            $"To restore, enter {previous.Value.X} / {previous.Value.Y} and apply again.",
            "Confirm mouse DPI change", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK || !_session.CanEditDpi) return;
        try { await HardwareCommandRunner.RunAsync(() => _session.ApplyDpiAsync(requested.X, requested.Y)); }
        catch (Exception ex) { _statusText.Text = "Failed · " + ex.Message; }
    }

    public bool IsReadOnlyQuerySource(DependencyObject? source)
    {
        if (IsPresetQuerySource(source)) return true;
        if (IsButtonQuerySource(source)) return true;
        while (source is not null && source != this)
        {
            if (ReferenceEquals(source, _refreshMouseButton)) return true;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }
}
