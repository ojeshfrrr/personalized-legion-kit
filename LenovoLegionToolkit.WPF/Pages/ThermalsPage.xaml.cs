using System;
using System.Threading.Tasks;
using System.Windows;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Settings;
using LenovoLegionToolkit.Lib.System;
using LenovoLegionToolkit.Lib.Utils;
using LenovoLegionToolkit.WPF.Utils;
using LenovoLegionToolkit.WPF.Windows.Dashboard;

namespace LenovoLegionToolkit.WPF.Pages;

public partial class ThermalsPage
{
    public ThermalsPage()
    {
        InitializeComponent();

        bool sensorsEnabled = IoCContainer.Resolve<ApplicationSettings>().Store.EnableHardwareSensors;
        // SensorsControl uses ISensorsController (Windows/NVAPI/Lenovo queries),
        // not the optional low-level SensorsGroupController. Match locked Home.
        _sensorsDisabledText.Visibility = sensorsEnabled ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void ThermalsPage_Initialized(object? sender, EventArgs e)
    {
        try
        {
        var machineInformationTask = Task.Run(Compatibility.GetMachineInformationAsync);
        var adapterStatusTask = Task.Run(Power.IsPowerAdapterConnectedAsync);
        await Task.WhenAll(machineInformationTask, adapterStatusTask);

        bool supportsFanSettings = machineInformationTask.Result.Properties.SupportsGodMode;
        bool isAdapterConnected = adapterStatusTask.Result != PowerAdapterStatus.Disconnected;
        _fanSettingsButton.IsEnabled = supportsFanSettings && isAdapterConnected;
        _fanSettingsStatus.Text = !supportsFanSettings
            ? "Custom fan curves are not supported by this device."
            : isAdapterConnected
                ? "Fan curves, power limits, and firmware-supported options are available in the existing settings panel."
                : "Connect the AC adapter to open custom fan and power settings.";
        }
        catch (Exception ex)
        {
            _fanSettingsButton.IsEnabled = false;
            _fanSettingsStatus.Text = "Cooling capabilities could not be read. Check device compatibility and driver availability.";
            Log.Instance.Trace($"Cooling capability detection failed.", ex);
        }
    }

    private void FanSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new GodModeSettingsWindow { Owner = Window.GetWindow(this) };
        window.ShowDialog();
    }
}
