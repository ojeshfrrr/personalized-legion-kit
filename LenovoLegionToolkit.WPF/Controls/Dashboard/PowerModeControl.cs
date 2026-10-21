using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Extensions;
using LenovoLegionToolkit.Lib.Features;
using LenovoLegionToolkit.Lib.Listeners;
using LenovoLegionToolkit.Lib.Settings;
using LenovoLegionToolkit.Lib.System;
using LenovoLegionToolkit.Lib.Utils;
using LenovoLegionToolkit.WPF.Resources;
using LenovoLegionToolkit.WPF.Utils;
using LenovoLegionToolkit.WPF.Windows.Dashboard;
using Wpf.Ui.Common;
using Button = Wpf.Ui.Controls.Button;

namespace LenovoLegionToolkit.WPF.Controls.Dashboard;

public class PowerModeControl : AbstractComboBoxFeatureCardControl<PowerModeState>
{
    private readonly ApplicationSettings _settings = IoCContainer.Resolve<ApplicationSettings>();
    private readonly ThermalModeListener _thermalModeListener = IoCContainer.Resolve<ThermalModeListener>();
    private readonly PowerModeListener _powerModeListener = IoCContainer.Resolve<PowerModeListener>();

    private readonly ThrottleLastDispatcher _throttleDispatcher = new(TimeSpan.FromMilliseconds(500), nameof(PowerModeControl));

    private readonly ListBox _modeTiles = new() { Tag = 2, Margin = new Thickness(0, 0, 0, 8) };
    private bool _tilesConfigured;
    private bool _useModeTiles;
    private bool CanInteractWithModes => _comboBox.IsEnabled && !AppFlags.Instance.ReadOnly && !AppFlags.Instance.UiPreview;

    // The selector projects the existing ComboBox items and selection. The existing
    // feature command, result verification and model-specific settings stay in use.
    public bool UseModeTiles
    {
        get => _useModeTiles;
        set
        {
            _useModeTiles = value;
            if (value) ConfigureModeTiles();
        }
    }

    private readonly Button _configButton = new()
    {
        Icon = SymbolRegular.Settings24,
        FontSize = 20,
        Margin = new(8, 0, 0, 0),
        Visibility = Visibility.Collapsed,
    };

    public PowerModeControl()
    {
        Icon = SymbolRegular.Gauge24;
        Title = Resource.PowerModeControl_Title;
        Subtitle = Resource.PowerModeControl_Message;

        AutomationProperties.SetName(_configButton, Resource.PowerModeControl_Title);

    }

    protected override void OnFinishedLoading()
    {
        base.OnFinishedLoading();
        _thermalModeListener.Changed += ThermalModeListener_Changed;
        _powerModeListener.Changed += PowerModeListener_Changed;
    }

    protected override void OnFinishedUnloading()
    {
        _thermalModeListener.Changed -= ThermalModeListener_Changed;
        _powerModeListener.Changed -= PowerModeListener_Changed;
        base.OnFinishedUnloading();
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        if (UseModeTiles) ConfigureModeTiles();
    }

    private void ConfigureModeTiles()
    {
        if (_tilesConfigured || Content is not UIElement original) return;
        _tilesConfigured = true;
        _modeTiles.SetResourceReference(StyleProperty, "ProjectLegionModeListStyle");
        _modeTiles.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(ComboBox.Items)) { Source = _comboBox });
        _modeTiles.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty,
            new Binding(nameof(ComboBox.SelectedItem)) { Source = _comboBox, Mode = BindingMode.TwoWay });
        _modeTiles.SizeChanged += (_, e) => _modeTiles.Tag = e.NewSize.Width < 360 ? 1 : 2;
        _comboBox.IsEnabledChanged += (_, _) => _modeTiles.IsEnabled = CanInteractWithModes;
        AutomationProperties.SetName(_modeTiles, Resource.PowerModeControl_Title);
        _modeTiles.IsEnabled = CanInteractWithModes;
        _comboBox.Visibility = Visibility.Collapsed;
        Content = null;
        var layout = new StackPanel();
        layout.Children.Add(_modeTiles);
        layout.Children.Add(original);
        Content = layout;
    }

    private void UpdateModeSummary()
    {
        if (!UseModeTiles) return;
        if (Confirmation.LastConfirmed is not { } state)
        {
            Subtitle = "Active mode has not been read from the device.";
            return;
        }
        Title = state.GetDisplayName();
        if (Confirmation.IsPending)
        {
            Subtitle = $"Applying {Confirmation.Requested?.GetDisplayName()}… Last confirmed: {state.GetDisplayName()}.";
            return;
        }
        if (Confirmation.Error is not null)
        {
            Subtitle = $"Change not verified. Last confirmed: {state.GetDisplayName()}.";
            return;
        }
        Subtitle = "Device-reported mode. " + (state switch
        {
            PowerModeState.Quiet => "Lower power and quieter cooling.",
            PowerModeState.Balance => "A balance of performance and acoustics.",
            PowerModeState.Performance => "Prioritize sustained performance. AC power is recommended.",
            PowerModeState.Extreme => "The device's highest performance mode. Availability depends on AC power.",
            PowerModeState.GodMode => "Open settings to adjust the cooling and power options supported by this device.",
            _ => Resource.PowerModeControl_Message
        });
    }

    protected override void OnConfirmationChanged() => UpdateModeSummary();

    private async void ThermalModeListener_Changed(object? sender, ThermalModeListener.ChangedEventArgs e) => await _throttleDispatcher.DispatchAsync(async () =>
    {
        await Dispatcher.InvokeAsync(async () =>
        {
            if (IsLoaded && IsVisible)
                await RefreshAsync();
        });
    });

    private async void PowerModeListener_Changed(object? sender, PowerModeListener.ChangedEventArgs e) => await _throttleDispatcher.DispatchAsync(async () =>
    {
        await Dispatcher.InvokeAsync(async () =>
        {
            if (IsLoaded && IsVisible)
                await RefreshAsync();
        });
    });

    protected override async Task OnRefreshAsync()
    {
        if (UseModeTiles) ConfigureModeTiles();
        await base.OnRefreshAsync();

        if (UseModeTiles)
        {
            _comboBox.Visibility = Visibility.Collapsed;
            _modeTiles.IsEnabled = CanInteractWithModes;
            UpdateModeSummary();
        }

        if (await Power.IsPowerAdapterConnectedAsync() != PowerAdapterStatus.Connected
            && TryGetSelectedItem(out var state)
            && state is PowerModeState.Performance or PowerModeState.GodMode or PowerModeState.Extreme)
            Warning = Resource.PowerModeControl_Warning;
        else
            Warning = string.Empty;
    }

    protected override async Task OnStateChangeAsync(ComboBox comboBox, IFeature<PowerModeState> feature, PowerModeState? newValue, PowerModeState? oldValue)
    {
        await base.OnStateChangeAsync(comboBox, feature, newValue, oldValue);

        if (Confirmation.LastConfirmed is not { } confirmedState)
        {
            return;
        }

        var mi = await Compatibility.GetMachineInformationAsync();
        var adapterStatus = await Power.IsPowerAdapterConnectedAsync();

        bool isAdapterConnected = adapterStatus != PowerAdapterStatus.Disconnected;

        bool shouldShowButton = confirmedState switch
        {
            PowerModeState.Balance when mi.Properties.SupportsAIMode => true,
            PowerModeState.GodMode when mi.Properties.SupportsGodMode => true,
            _ => false
        } && isAdapterConnected;

        _configButton.ToolTip = shouldShowButton ? Resource.PowerModeControl_Settings : null;
        _configButton.Visibility = shouldShowButton ? Visibility.Visible : Visibility.Collapsed;
        UpdateModeSummary();
    }

    protected override void OnStateChangeException(Exception exception)
    {
        if (exception is PowerModeUnavailableWithoutACException ex1)
        {
            SnackbarHelper.Show(Resource.PowerModeUnavailableWithoutACException_Title,
                string.Format(Resource.PowerModeUnavailableWithoutACException_Message, ex1.PowerMode.GetDisplayName()),
                SnackbarType.Warning);
        }
    }

    protected override FrameworkElement GetAccessory(ComboBox comboBox)
    {
        _configButton.Click += ConfigButton_Click;

        var stackPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
        };
        stackPanel.Children.Add(_configButton);
        stackPanel.Children.Add(comboBox);

        return stackPanel;
    }

    private async void ConfigButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedItem(out var state))
        {
            return;
        }

        switch (state)
        {
            case PowerModeState.Balance:
            {
                var window = new BalanceModeSettingsWindow { Owner = Window.GetWindow(this) };
                window.ShowDialog();
                break;
            }
            case PowerModeState.GodMode:
            {
                var result = await CheckCustomModeWarningAsync();
                if (!result)
                {
                    return;
                }

                var window = new GodModeSettingsWindow { Owner = Window.GetWindow(this) };
                window.ShowDialog();
                break;
            }
            default:
                throw new Exception($"Access to Settings in {state} is denied.");
        }
    }

    private async Task<bool> CheckCustomModeWarningAsync()
    {
        if (_settings.Store.CustomModeWarningDontShowAgain)
        {
            return true;
        }

        try
        {
            var result = await ShowDialogAsync();

            if (result is { DontShowAgain: true, Yes: true })
            {
                _settings.Store.CustomModeWarningDontShowAgain = true;
                _settings.SynchronizeStore();
                return true;
            }

            if (result.Yes)
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Failed to load power mode control", ex);
        }

        return false;
    }

    private async Task<(bool Yes, bool DontShowAgain)> ShowDialogAsync()
    {
        return await await Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            var result = await MessageBoxHelper.ShowAsync(
                this,
                Resource.Warning,
                Resource.GodModeSettingsWindow_CustomMode_Warning,
                true,
                Resource.Yes,
                Resource.No);

            return (result.Result, result.DontShowAgain);
        });
    }
}
