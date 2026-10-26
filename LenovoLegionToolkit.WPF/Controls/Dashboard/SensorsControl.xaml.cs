using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Controllers.Sensors;
using LenovoLegionToolkit.Lib.Settings;
using LenovoLegionToolkit.Lib.Utils;
using LenovoLegionToolkit.WPF.Resources;
using LenovoLegionToolkit.WPF.Utils;
using MenuItem = Wpf.Ui.Controls.MenuItem;

namespace LenovoLegionToolkit.WPF.Controls.Dashboard;

public partial class SensorsControl
{
    private readonly ISensorsController _controller = IoCContainer.Resolve<ISensorsController>();
    private readonly ApplicationSettings _applicationSettings = IoCContainer.Resolve<ApplicationSettings>();

    private readonly DemandReadSession<SensorsData> _reads;
    private readonly OptionalUiActivity _activity;
    private bool _prepared;

    public SensorsControl()
    {
        InitializeComponent();
        _reads = new DemandReadSession<SensorsData>(async token =>
        {
            token.ThrowIfCancellationRequested();
            if (!_prepared)
            {
                if (!await _controller.IsSupportedAsync()) throw new NotSupportedException();
                token.ThrowIfCancellationRequested();
                await _controller.PrepareAsync();
                token.ThrowIfCancellationRequested();
                _prepared = true;
            }
            token.ThrowIfCancellationRequested();
            var data = await _controller.GetDataAsync();
            token.ThrowIfCancellationRequested();
            return data;
        }, (data, time) =>
        {
            UpdateValues(data);
            _readStatusText.Text = $"Read {time:HH:mm:ss}";
            ReadOnlyQaSession.Mark("FirstSensorSample");
        }, ex =>
        {
            UpdateValues(SensorsData.Empty);
            _readStatusText.Text = "Readings unavailable; monitoring resumes while this page is active.";
            Log.Instance.Trace($"Sensor snapshot unavailable.", ex);
        }, refreshInterval: TimeSpan.FromSeconds(1));
        _activity = new OptionalUiActivity(this, active =>
        {
            // The session owns/drains the task; do not block visibility cancellation.
            if (active) { _prepared = false; _ = _reads.ActivateAsync(); }
            else _ = _reads.DeactivateAsync();
            return Task.CompletedTask;
        }, suspendForGaming: false); // Visible foreground readings remain available while a game runs.
        ContextMenu = new ContextMenu();
        var refresh = new MenuItem { Header = "Refresh readings", Tag = "ReadOnlyRefresh" };
        refresh.Click += ReadRefresh_Click;
        ContextMenu.Items.Add(refresh);
        ContextMenu.Items.Add(new MenuItem { Header = "Live while this window is active · 1 second", IsEnabled = false });
    }

    private async void ReadRefresh_Click(object sender, RoutedEventArgs e) => await _reads.RefreshAsync();

    private void UpdateValues(SensorsData data)
    {
        UpdateValue(_cpuUtilizationBar, _cpuUtilizationLabel, data.CPU.MaxUtilization, data.CPU.Utilization,
            $"{data.CPU.Utilization}{Resource.Percent}");
        UpdateValue(_cpuCoreClockBar, _cpuCoreClockLabel, data.CPU.MaxCoreClock, data.CPU.CoreClock,
            $"{data.CPU.CoreClock / 1000.0:0.0} {Resource.GHz}", $"{data.CPU.MaxCoreClock / 1000.0:0.0} {Resource.GHz}");
        UpdateValue(_cpuTemperatureBar, _cpuTemperatureLabel, data.CPU.MaxTemperature, data.CPU.Temperature,
            GetTemperatureText(data.CPU.Temperature), GetTemperatureText(data.CPU.MaxTemperature));
        UpdateValue(_cpuFanSpeedBar, _cpuFanSpeedLabel, data.CPU.MaxFanSpeed, data.CPU.FanSpeed,
            $"{data.CPU.FanSpeed} {Resource.RPM}", $"{data.CPU.MaxFanSpeed} {Resource.RPM}");

        UpdateValue(_gpuUtilizationBar, _gpuUtilizationLabel, data.GPU.MaxUtilization, data.GPU.Utilization,
            $"{data.GPU.Utilization} {Resource.Percent}");
        UpdateValue(_gpuCoreClockBar, _gpuCoreClockLabel, data.GPU.MaxCoreClock, data.GPU.CoreClock,
            $"{data.GPU.CoreClock} {Resource.MHz}", $"{data.GPU.MaxCoreClock} {Resource.MHz}");
        UpdateValue(_gpuMemoryClockBar, _gpuMemoryClockLabel, data.GPU.MaxMemoryClock, data.GPU.MemoryClock,
            $"{data.GPU.MemoryClock} {Resource.MHz}", $"{data.GPU.MaxMemoryClock} {Resource.MHz}");
        UpdateValue(_gpuTemperatureBar, _gpuTemperatureLabel, data.GPU.MaxTemperature, data.GPU.Temperature,
            GetTemperatureText(data.GPU.Temperature), GetTemperatureText(data.GPU.MaxTemperature));
        UpdateValue(_gpuFanSpeedBar, _gpuFanSpeedLabel, data.GPU.MaxFanSpeed, data.GPU.FanSpeed,
            $"{data.GPU.FanSpeed} {Resource.RPM}", $"{data.GPU.MaxFanSpeed} {Resource.RPM}");
    }

    private string GetTemperatureText(double temperature)
    {
        if (double.IsNaN(temperature) || temperature < 0) return "-";

        if (_applicationSettings.Store.TemperatureUnit == TemperatureUnit.F)
        {
            var fahrenheit = temperature * 9.0 / 5.0 + 32.0;
            return $"{fahrenheit:0} {Resource.Fahrenheit}";
        }

        return $"{temperature:0} {Resource.Celsius}";
    }

    private static void UpdateValue(RangeBase bar, ContentControl label, double max, double value, string text, string? toolTipText = null)
    {
        if (double.IsNaN(max) || double.IsNaN(value) || max < 0 || value < 0)
        {
            bar.Minimum = 0;
            bar.Maximum = 1;
            bar.Value = 0;
            label.Content = "-";
            label.ToolTip = null;
            label.Tag = 0;
        }
        else
        {
            bar.Minimum = 0;
            bar.Maximum = max;
            bar.Value = value;
            label.Content = text;
            label.ToolTip = toolTipText is null ? null : string.Format(Resource.SensorsControl_Maximum, toolTipText);
            label.Tag = value;
        }
    }
}
