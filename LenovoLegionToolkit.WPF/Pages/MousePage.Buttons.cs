using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Services.Mouse;
using LenovoLegionToolkit.WPF.Presentation;
using LenovoLegionToolkit.WPF.Utils;

namespace LenovoLegionToolkit.WPF.Pages;

public partial class MousePage
{
    private MouseButtonsSession? _buttons;
    private MouseProfileBanks? _displayedBanks;
    private bool _renderingButtons;
    private enum ButtonDraftType { Basic, Keyboard }
    private sealed record DraftTypeChoice(ButtonDraftType Type, string Label);
    private sealed record BankChoice(byte Id) { public string Label => $"Device bank {Id}"; }
    private sealed record ButtonActionChoice(MouseButtonAction Action) { public string Label => DeathAdderV3ButtonProtocol.ActionLabel(Action); }
    private sealed record KeyboardKeyChoice(MouseKeyboardKey Key) { public string Label => Key.ToString().Replace("Digit", "", StringComparison.Ordinal); }
    private sealed record ButtonRow(byte Id, string Name, string Assignment);
    private void InitializeButtonUi(IMouseButtonService service)
    {
        _buttons = new(service, CanChangeIndividual);
        _buttonTargetSelector.ItemsSource = DeathAdderV3ButtonProtocol.Buttons;
        _buttonActionSelector.ItemsSource = DeathAdderV3ButtonProtocol.Actions.Select(a => new ButtonActionChoice(a)).ToArray();
        _buttonKeyboardKey.ItemsSource = MouseKeyboardAssignments.Keys.Select(k => new KeyboardKeyChoice(k)).ToArray();
        _buttonDraftKind.ItemsSource = new[] { new DraftTypeChoice(ButtonDraftType.Basic, "Mouse / DPI action"), new DraftTypeChoice(ButtonDraftType.Keyboard, "Keyboard key") };
        _buttonDraftKind.SelectedIndex = 0;
        _buttons.Changed += RenderButtons;
        Loaded += (_, _) => _buttons.Activate();
        Unloaded += (_, _) => _buttons.Deactivate();
        RenderButtons();
    }
    private void RenderButtons()
    {
        if (_buttons is null) return;
        _renderingButtons = true;
        try
        {
            if (!ReferenceEquals(_displayedBanks, _buttons.Banks))
            {
                _displayedBanks = _buttons.Banks;
                _buttonProfileSelector.ItemsSource = _buttons.Banks?.Ids.Select(id => new BankChoice(id)).ToArray();
                _buttonProfileSelector.SelectedItem = _buttonProfileSelector.Items.Cast<BankChoice>().FirstOrDefault(p => p.Id == _buttons.SelectedBank);
            }
            _buttonLayout.ItemsSource = DeathAdderV3ButtonProtocol.Buttons.Select(b => new ButtonRow(b.Id, $"{b.Name} · ID {b.Id}",
                _buttons.Current?.Assignments.SingleOrDefault(a => a.Button == b.Id)?.Description ?? "Unavailable · read assignments")).ToArray();
            _buttonStatus.Text = _buttons.Status;
            _buttonLastConfirmed.Text = _buttons.LastReadAt is { } time && _buttons.LastConfirmed is { } bank
                ? $"Last full bank read: bank {bank.Profile} · {time:HH:mm:ss}. Historical when current readings are unavailable."
                : "No confirmed assignment read yet.";
        }
        finally { _renderingButtons = false; }
        RefreshButtonAccess();
        RefreshPresetAccess();
    }
    private MouseKeyboardModifiers DraftModifiers() =>
        (_keyLeftCtrl.IsChecked == true ? MouseKeyboardModifiers.LeftCtrl : 0) |
        (_keyLeftShift.IsChecked == true ? MouseKeyboardModifiers.LeftShift : 0) |
        (_keyLeftAlt.IsChecked == true ? MouseKeyboardModifiers.LeftAlt : 0) |
        (_keyLeftWin.IsChecked == true ? MouseKeyboardModifiers.LeftWin : 0) |
        (_keyRightCtrl.IsChecked == true ? MouseKeyboardModifiers.RightCtrl : 0) |
        (_keyRightShift.IsChecked == true ? MouseKeyboardModifiers.RightShift : 0) |
        (_keyRightAlt.IsChecked == true ? MouseKeyboardModifiers.RightAlt : 0) |
        (_keyRightWin.IsChecked == true ? MouseKeyboardModifiers.RightWin : 0);
    private bool TryButtonDraft(out MouseButtonBinding? binding)
    {
        binding = null;
        if (_buttonDraftKind.SelectedItem is not DraftTypeChoice kind) return false;
        if (kind.Type == ButtonDraftType.Basic && _buttonActionSelector.SelectedItem is ButtonActionChoice action)
            binding = MouseButtonBinding.Basic(action.Action);
        else if (kind.Type == ButtonDraftType.Keyboard && _buttonKeyboardKey.SelectedItem is KeyboardKeyChoice key)
            binding = MouseButtonBinding.Key(new(key.Key, DraftModifiers()));
        if (binding is null) return false;
        try { binding.Validate(); return true; } catch (ArgumentException) { binding = null; return false; }
    }
    private void RefreshButtonAccess()
    {
        if (_buttons is null || _applyButtonAssignment is null) return;
        var idle = _buttons.IsActive && !_buttons.IsBusy && !PresetBusy;
        _readButtonProfiles.IsEnabled = idle;
        _buttonProfileSelector.IsEnabled = idle && _buttons.Banks?.Ids.Count > 0;
        _readButtonAssignments.IsEnabled = idle && _buttons.SelectedBank.HasValue;
        _buttonTargetSelector.IsEnabled = idle && _buttons.Current is not null;
        var target = _buttonTargetSelector.SelectedItem as MouseButtonDefinition;
        _selectedButtonTitle.Text = target is null ? "Select a button" : target.Name + (target.Id == 1 ? " · protected" : "");
        foreach (var region in _mouseSchematic.Children.OfType<System.Windows.Controls.RadioButton>())
        {
            region.IsEnabled = idle && _buttons.Current is not null;
            region.IsChecked = target is not null && byte.TryParse(region.Tag.ToString(), out var id) && id == target.Id;
        }
        var current = target is null ? null : _buttons.Current?.Assignments.SingleOrDefault(a => a.Button == target.Id);
        var allowed = idle && target is not null && _buttons.CanApply(target.Id);
        var keyboard = (_buttonDraftKind.SelectedItem as DraftTypeChoice)?.Type == ButtonDraftType.Keyboard;
        _buttonDraftKind.IsEnabled = allowed;
        _buttonActionSelector.IsEnabled = allowed && !keyboard;
        _buttonActionSelector.Visibility = keyboard ? Visibility.Collapsed : Visibility.Visible;
        _keyboardAssignmentOptions.IsEnabled = allowed && keyboard;
        _keyboardAssignmentOptions.Visibility = keyboard ? Visibility.Visible : Visibility.Collapsed;
        var valid = TryButtonDraft(out var draft);
        _applyButtonAssignment.IsEnabled = allowed && valid && current?.KnownBinding is { } original && draft != original;
        _restoreButtonAssignment.IsEnabled = !PresetBusy && _buttons.CanRestore;
        _buttonRequestedValue.Text = valid ? $"Requested (not applied): {draft!.Description}" : "Requested (not applied): choose a supported assignment.";
        var last = target is null ? null : _buttons.LastConfirmed?.Assignments.SingleOrDefault(a => a.Button == target.Id);
        var stamp = target is null ? null : _buttons.ConfirmedAt(target.Id);
        _buttonConfirmedValue.Text = current is not null ? $"Last confirmed: {current.Description} · {stamp:HH:mm:ss}"
            : last is not null ? $"Historical bank {last.Profile}: {last.Description} · {stamp:HH:mm:ss}. Current assignment unavailable."
            : "Last confirmed: unavailable for the selected button.";
        _buttonRestoreInfo.Text = _buttons.RestorePoint is { } point
            ? $"Previous assignment for button {point.Original.Button}: {point.Original.Description}. Restore is explicit and requires a matching fresh read."
            : "No restorable assignment change in this session.";
    }
    private async void ReadButtonProfiles_Click(object sender, RoutedEventArgs e) { if (_buttons is not null && !PresetBusy) await _buttons.ReadProfilesAsync(); }
    private async void ReadButtonAssignments_Click(object sender, RoutedEventArgs e) { if (_buttons is not null && !PresetBusy) await _buttons.ReadAssignmentsAsync(); }
    private void ButtonProfile_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_renderingButtons && _buttonProfileSelector.SelectedItem is BankChoice bank) _buttons?.SelectBank(bank.Id);
    }
    private void ButtonDraft_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_buttons is null || _renderingButtons) return;
        if (ReferenceEquals(sender, _buttonTargetSelector) && _buttonTargetSelector.SelectedItem is MouseButtonDefinition target)
        {
            var known = _buttons.Current?.Assignments.SingleOrDefault(a => a.Button == target.Id);
            _renderingButtons = true;
            try
            {
                _buttonActionSelector.SelectedItem = _buttonActionSelector.Items.Cast<ButtonActionChoice>().FirstOrDefault(c => c.Action == known?.KnownAction);
                _buttonDraftKind.SelectedIndex = known?.KnownKeyboard is not null ? 1 : 0;
                _buttonKeyboardKey.SelectedItem = _buttonKeyboardKey.Items.Cast<KeyboardKeyChoice>().FirstOrDefault(k => k.Key == known?.KnownKeyboard?.Key);
                var flags = known?.KnownKeyboard?.Modifiers ?? MouseKeyboardModifiers.None;
                _keyLeftCtrl.IsChecked = flags.HasFlag(MouseKeyboardModifiers.LeftCtrl); _keyLeftShift.IsChecked = flags.HasFlag(MouseKeyboardModifiers.LeftShift);
                _keyLeftAlt.IsChecked = flags.HasFlag(MouseKeyboardModifiers.LeftAlt); _keyLeftWin.IsChecked = flags.HasFlag(MouseKeyboardModifiers.LeftWin);
                _keyRightCtrl.IsChecked = flags.HasFlag(MouseKeyboardModifiers.RightCtrl); _keyRightShift.IsChecked = flags.HasFlag(MouseKeyboardModifiers.RightShift);
                _keyRightAlt.IsChecked = flags.HasFlag(MouseKeyboardModifiers.RightAlt); _keyRightWin.IsChecked = flags.HasFlag(MouseKeyboardModifiers.RightWin);
            }
            finally { _renderingButtons = false; }
        }
        RefreshButtonAccess(); // Draft changes never send device commands.
    }
    private void KeyboardModifier_Changed(object sender, RoutedEventArgs e)
    {
        if (!_renderingButtons) RefreshButtonAccess();
    }
    private void SelectSchematicButton(object? tag)
    {
        if (_buttons?.IsActive != true || _buttons.IsBusy || PresetBusy || _buttons.Current is null ||
            !byte.TryParse(tag?.ToString(), out var id)) return;
        _buttonTargetSelector.SelectedItem = DeathAdderV3ButtonProtocol.Buttons.SingleOrDefault(b => b.Id == id);
        // Selection changes only the local draft. It never applies a mapping.
    }
    private void Schematic_Click(object sender, RoutedEventArgs e) =>
        SelectSchematicButton((sender as FrameworkElement)?.Tag);
    private void AssignmentRow_Click(object sender, RoutedEventArgs e) =>
        SelectSchematicButton((sender as FrameworkElement)?.Tag);
    private async void ApplyButtonAssignment_Click(object sender, RoutedEventArgs e)
    {
        if (_buttons is null || _buttonTargetSelector.SelectedItem is not MouseButtonDefinition target || !_buttons.CanApply(target.Id) ||
            !TryButtonDraft(out var requested) || requested is null) return;
        var previous = _buttons.Current!.Assignments.Single(a => a.Button == target.Id);
        if (previous.KnownBinding == requested) return;
        var answer = System.Windows.MessageBox.Show(Window.GetWindow(this),
            $"Change {target.Name} in bank 1 from {previous.Description} to {requested.Description}?\n\n" +
            "Only this normal-layer assignment is sent. Primary left click is protected. " +
            "A matching fresh original, validated acknowledgement and independent readback are required. " +
            (requested.Keyboard is not null ? "Keyboard assignments have passed offline tests but have not been physically tested. " : "Back/Forward remapping and restoration have been physically verified. ") +
            "Reconnect persistence is unverified. No automatic retry or restoration is performed.",
            "Confirm mouse assignment", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK || !_buttons.CanApply(target.Id)) return;
        try
        {
            await HardwareCommandRunner.RunAsync(() => requested.Action is { } action
                ? _buttons.ApplyAsync(target.Id, action) : _buttons.ApplyKeyboardAsync(target.Id, requested.Keyboard!));
        }
        catch (Exception ex) { _buttonStatus.Text = "Failed · " + ex.Message; }
    }
    private async void RestoreButtonAssignment_Click(object sender, RoutedEventArgs e)
    {
        if (_buttons?.RestorePoint is not { } point || !_buttons.CanRestore) return;
        var answer = System.Windows.MessageBox.Show(Window.GetWindow(this),
            $"Restore button {point.Original.Button} from {point.Changed.Description} to {point.Original.Description}?\n\n" +
            "The device must still match the last confirmed change. Restoration is acknowledged and read back; it is never assumed.",
            "Confirm assignment restoration", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK || !_buttons.CanRestore) return;
        try { await HardwareCommandRunner.RunAsync(() => _buttons.RestoreAsync()); }
        catch (Exception ex) { _buttonStatus.Text = "Restoration not confirmed · " + ex.Message; }
    }
    private bool IsButtonQuerySource(DependencyObject? source)
    {
        while (source is not null && source != this)
        {
            if (ReferenceEquals(source, _readButtonProfiles) || ReferenceEquals(source, _readButtonAssignments) ||
                ReferenceEquals(source, _buttonProfileSelector) || ReferenceEquals(source, _buttonTargetSelector) ||
                ReferenceEquals(source, _mouseSchematic) || ReferenceEquals(source, _buttonLayout)) return true;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }
}
