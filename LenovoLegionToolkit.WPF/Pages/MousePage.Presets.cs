using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LenovoLegionToolkit.Lib.Services.Mouse;
using LenovoLegionToolkit.Lib.Utils;
using LenovoLegionToolkit.WPF.Utils;

namespace LenovoLegionToolkit.WPF.Pages;

public partial class MousePage
{
    private readonly MouseSoftwareProfileStore _presetStore = new();
    private readonly MousePresetApplier _presetApplier;
    private MouseSoftwareProfile? _selectedPreset;
    private CancellationTokenSource? _presetLifetime;
    private Task _presetApplyTask = Task.CompletedTask;
    private Task _presetStorageTask = Task.CompletedTask;
    private bool _presetApplying, _presetStorageBusy, _renderingPresets;
    private bool PresetBusy => _presetApplying || _presetStorageBusy;
    private bool CanChangeIndividual() => CanWrite() && !PresetBusy;
    private static bool CanPersistPresets() => !Folders.IsReadOnlySession && !AppFlags.Instance.ReadOnly && !AppFlags.Instance.UiPreview;
    private bool IsPresetCurrent(CancellationTokenSource lifetime) => ReferenceEquals(lifetime, _presetLifetime) && !lifetime.IsCancellationRequested;
    private CancellationTokenSource ActivatePresets() => _presetLifetime ??= new();
    private void DeactivatePresets()
    {
        var lifetime = _presetLifetime;
        _presetLifetime = null;
        lifetime?.Cancel();
        if (!_presetApplying) lifetime?.Dispose();
    }

    private void RefreshPresetAccess()
    {
        if (_presetSelector is null) return;
        var active = _presetLifetime is not null;
        var idle = active && !PresetBusy && !_session.IsBusy && _buttons?.IsBusy != true;
        var files = idle && CanPersistPresets();
        _presetSelector.IsEnabled = _presetReload.IsEnabled = idle;
        _presetName.IsEnabled = _presetIncludeAssignments.IsEnabled = files;
        _presetSave.IsEnabled = files && _session.Dpi.Current is not null && _session.Polling.Current is not null;
        _presetRename.IsEnabled = _presetDelete.IsEnabled = files && _selectedPreset is not null;
        _presetApply.IsEnabled = idle && CanWrite() && _selectedPreset is not null;
        _presetPolicy.Text = CanPersistPresets()
            ? "Stored on this PC only. Selecting or saving never changes the mouse. Apply requires enabled hardware controls."
            : "Read-only: local Save, Rename, Delete and hardware Apply are locked. Stored presets remain inspectable.";
    }

    private Task LoadPresetsAsync() => RunPresetStorageAsync(null, _selectedPreset?.Id, "Local presets loaded. No device command sent.");
    private Task RunPresetStorageAsync(Action? mutation, Guid? selected, string success)
    {
        if (_presetLifetime is not { } lifetime || PresetBusy || _session.IsBusy || _buttons?.IsBusy == true)
            return _presetStorageTask;
        _presetStorageBusy = true;
        _presetStatus.Text = mutation is null ? "Reading local presets…" : "Updating local preset file…";
        RefreshControlAccess();
        return _presetStorageTask = RunAsync();
        async Task RunAsync()
        {
            try
            {
                var profiles = await Task.Run(() => { mutation?.Invoke(); return _presetStore.Load(); });
                if (!IsPresetCurrent(lifetime)) return;
                _renderingPresets = true;
                try
                {
                    _presetSelector.ItemsSource = profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
                    _presetSelector.SelectedItem = _presetSelector.Items.Cast<MouseSoftwareProfile>().FirstOrDefault(p => p.Id == selected);
                }
                finally { _renderingPresets = false; }
                RenderPresetSelection();
                _presetStatus.Text = success;
            }
            catch (Exception ex)
            {
                if (!IsPresetCurrent(lifetime)) return;
                _presetSelector.ItemsSource = Array.Empty<MouseSoftwareProfile>();
                RenderPresetSelection();
                _presetStatus.Text = "Local preset operation/read not completed; reload to verify. " + ex.Message;
            }
            finally
            {
                _presetStorageBusy = false;
                if (IsPresetCurrent(lifetime)) RefreshControlAccess();
            }
        }
    }
    private async void PresetReload_Click(object sender, RoutedEventArgs e) => await LoadPresetsAsync();
    private void PresetSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_renderingPresets) RenderPresetSelection(); // Local inspection only; no device calls.
    }
    private void RenderPresetSelection()
    {
        _selectedPreset = _presetSelector.SelectedItem as MouseSoftwareProfile;
        _presetName.Text = _selectedPreset?.Name ?? "";
        _presetStored.Text = _selectedPreset is { } p ? DescribePreset(p)
            : "No local preset selected. Read the device before saving confirmed settings.";
        _presetInspector.IsExpanded = _selectedPreset is not null;
        RefreshPresetAccess();
    }
    private static string DescribePreset(MouseSoftwareProfile p) =>
        $"Stored intent · {p.Name}\nDPI: {p.Dpi.X:N0} / {p.Dpi.Y:N0}\nPolling: {(int)p.PollingRate:N0} Hz\n" +
        (p.Assignments.Count == 0 ? "Button assignments: not included."
            : string.Join("\n", p.Assignments.OrderBy(a => a.Key).Select(a => $"Button {a.Key}: {a.Value.Description}"))) +
        "\nLocal software data; not current device state or an onboard profile.";
    private async void PresetSave_Click(object sender, RoutedEventArgs e)
    {
        if (!CanPersistPresets() || PresetBusy || _session.IsBusy || _buttons?.IsBusy == true ||
            _session.Dpi.Current is not { } dpi || _session.Polling.Current is not { } polling) return;
        try
        {
            var bindings = new Dictionary<byte, MouseButtonBinding>();
            if (_presetIncludeAssignments.IsChecked == true)
            {
                if (_buttons?.Current is not { Profile: 1 } bank)
                    throw new InvalidOperationException("Read bank 1 assignments first, or uncheck inclusion. Other banks are not saved as preset intent.");
                foreach (var assignment in bank.Assignments.Where(a => a.Button != 1))
                {
                    if (assignment.KnownBinding is not { } binding)
                        throw new InvalidOperationException($"Button {assignment.Button} has an unsupported assignment. Uncheck inclusion; no value will be guessed.");
                    bindings.Add(assignment.Button, binding);
                }
            }
            var preset = MouseSoftwareProfileStore.ValidateAndCopy(new(Guid.NewGuid(), _presetName.Text, dpi.Value, polling.Value, bindings));
            if (MessageBox.Show(Window.GetWindow(this), DescribePreset(preset) +
                $"\n\nDPI read: {dpi.ReadAt:HH:mm:ss}. Polling read: {polling.ReadAt:HH:mm:ss}." +
                (bindings.Count == 0 ? "" : $" Assignment bank read: {_buttons!.LastReadAt:HH:mm:ss}.") +
                "\nSave these last-confirmed readings as a NEW local preset? No hardware command will be sent.",
                "Save local software preset", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
            await RunPresetStorageAsync(() => _presetStore.Save(preset), preset.Id, "Local preset saved. The mouse was not changed.");
        }
        catch (Exception ex) { _presetStatus.Text = "Preset not saved. " + ex.Message; }
    }
    private async void PresetRename_Click(object sender, RoutedEventArgs e)
    {
        if (!CanPersistPresets() || PresetBusy || _selectedPreset is not { } preset) return;
        var name = _presetName.Text;
        await RunPresetStorageAsync(() => _presetStore.Rename(preset.Id, name), preset.Id, "Local preset renamed. Stored settings and hardware unchanged.");
    }
    private async void PresetDelete_Click(object sender, RoutedEventArgs e)
    {
        if (!CanPersistPresets() || PresetBusy || _selectedPreset is not { } preset) return;
        if (MessageBox.Show(Window.GetWindow(this), $"Delete local preset ‘{preset.Name}’ from this PC?\n\nNo onboard bank or mouse setting will be changed.",
            "Delete local software preset", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        await RunPresetStorageAsync(() =>
        {
            if (!_presetStore.Delete(preset.Id)) throw new InvalidOperationException("The preset was already removed. Reload the list.");
        }, null, "Local preset deleted. The mouse was not changed.");
    }
    private async void PresetApply_Click(object sender, RoutedEventArgs e)
    {
        if (!CanWrite() || PresetBusy || _session.IsBusy || _buttons?.IsBusy == true ||
            _presetLifetime is not { } lifetime || _selectedPreset is not { } preset) return;
        MouseSoftwareProfile snapshot;
        try { snapshot = MouseSoftwareProfileStore.ValidateAndCopy(preset); }
        catch (Exception ex) { _presetStatus.Text = "Invalid preset. " + ex.Message; return; }
        if (MessageBox.Show(Window.GetWindow(this), DescribePreset(snapshot) +
            "\n\nApply in order: DPI, polling, then listed buttons (bank 1, normal layer). Unlisted buttons and DPI stages are not edited. " +
            "Fresh baseline reads precede any write; each operation requires verified device readback. " +
            "Stop on first failure or ambiguous result. Earlier settings may already have changed; no automatic retry or rollback. " +
            "Device persistence is unverified. Keyboard mappings have not been physically tested.",
            "Apply local software preset to mouse", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        if (!IsPresetCurrent(lifetime) || !CanWrite() || PresetBusy || _session.IsBusy || _buttons?.IsBusy == true) return;
        _presetApplyTask = ApplyPresetAsync(snapshot, lifetime);
        await _presetApplyTask;
    }
    private async Task ApplyPresetAsync(MouseSoftwareProfile preset, CancellationTokenSource lifetime)
    {
        _presetApplying = true;
        _presetStatus.Text = "Validating and applying sequentially… No automatic retries or rollback.";
        _session.InvalidateForPreset();
        if (preset.Assignments.Count > 0) _buttons?.InvalidateForPreset();
        RefreshControlAccess();
        try
        {
            MousePresetApplyResult? result = null;
            await HardwareCommandRunner.RunAsync(async () => result = await _presetApplier.ApplyAsync(preset, lifetime.Token));
            if (result is null) throw new InvalidOperationException("No preset result was returned.");
            // Retain the ledger even when navigation cancelled the batch; never
            // accept its readings into a later activation of the page.
            _presetResult.Text = $"Last apply · {preset.Name} · {DateTimeOffset.Now:g}\n" + result.Summary;
            _presetStatus.Text = result.Success ? "Preset verified setting by setting. No onboard profile saved."
                : "Stopped: preset not fully applied. Inspect confirmed, uncertain and not-attempted steps below.";
            if (!IsPresetCurrent(lifetime)) return;
            _session.AcceptPresetResult(result);
            if (preset.Assignments.Count > 0) _buttons?.AcceptPresetResult(result);
            _presetInspector.IsExpanded = true;
        }
        catch (Exception ex)
        {
            _presetResult.Text = $"Last apply · {preset.Name} · {DateTimeOffset.Now:g}\nPreset did not complete. Read the device before further changes. " + ex.Message;
            _presetStatus.Text = "Preset not completed; inspect the result before further changes.";
            _presetInspector.IsExpanded = true;
        }
        finally
        {
            _presetApplying = false;
            if (IsPresetCurrent(lifetime)) RefreshControlAccess();
            else lifetime.Dispose();
        }
    }
    private bool IsPresetQuerySource(DependencyObject? source)
    {
        while (source is not null && source != this)
        {
            if (ReferenceEquals(source, _presetSelector) || ReferenceEquals(source, _presetReload) ||
                ReferenceEquals(source, _presetInspector)) return true;
            // Local file edits are safe with hardware controls locked, but are
            // never enabled for --read-only / --ui-preview configuration sessions.
            if (CanPersistPresets() && (ReferenceEquals(source, _presetName) || ReferenceEquals(source, _presetSave) ||
                ReferenceEquals(source, _presetRename) || ReferenceEquals(source, _presetDelete) ||
                ReferenceEquals(source, _presetIncludeAssignments))) return true;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false; // Apply Preset is intentionally not a read-only query source.
    }
}
