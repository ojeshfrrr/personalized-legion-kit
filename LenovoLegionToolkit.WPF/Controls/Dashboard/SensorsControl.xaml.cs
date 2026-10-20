using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Humanizer;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Controllers;
using LenovoLegionToolkit.Lib.Controllers.Sensors;
using LenovoLegionToolkit.Lib.Settings;
using LenovoLegionToolkit.Lib.Utils;
using LenovoLegionToolkit.WPF.Resources;
using LenovoLegionToolkit.WPF.Settings;
using LenovoLegionToolkit.WPF.Utils;
using Wpf.Ui.Common;
using MenuItem = Wpf.Ui.Controls.MenuItem;

namespace LenovoLegionToolkit.WPF.Controls.Dashboard;

public partial class SensorsControl
{
    private readonly ISensorsController _controller = IoCContainer.Resolve<ISensorsController>();
    private readonly ApplicationSettings _applicationSettings = IoCContainer.Resolve<ApplicationSettings>();
    private readonly DashboardSettings _dashboardSettings = IoCContainer.Resolve<DashboardSettings>();

    private CancellationTokenSource? _cts;
    private Task? _refreshTask;
    private readonly OptionalUiActivity _activity;
    private IDisposable? _gpuMonitoringLease;

    public SensorsControl()
    {
        InitializeComponent();
        InitializeContextMenu();

        _activity = new OptionalUiActivity(this, SetActivityAsync);
    }

    private void InitializeContextMenu()
    {
        ContextMenu = new ContextMenu();
        ContextMenu.Items.Add(new MenuItem { Header = Resource.SensorsControl_RefreshInterval, IsEnabled = false });

        foreach (var interval in new double[] { 0.5, 1, 2, 3, 4, 5 })
        {
            var item = new MenuItem
            {
                SymbolIcon = _dashboardSettings.Store.SensorsRefreshIntervalSeconds == interval ? SymbolRegular.Checkmark24 : SymbolRegular.Empty,
                Header = TimeSpan.FromSeconds(interval).Humanize(culture: Resource.Culture)
            };
            item.Click += (_, _) =>
            {
                _dashboardSettings.Store.SensorsRefreshIntervalSeconds = interval;
                _dashboardSettings.SynchronizeStore();
                InitializeContextMenu();
            };
            ContextMenu.Items.Add(item);
        }
    }

    private async Task SetActivityAsync(bool active)
    {
        if (active)
        {
            _gpuMonitoringLease ??= IoCContainer.Resolve<GPUController>().AcquireOptionalMonitoring();
            Refresh();
            return;
        }

        var cts = _cts;
        var refreshTask = _refreshTask;
        _cts = null;
        _refreshTask = null;
        if (cts is not null)
            await cts.CancelAsync();
        if (refreshTask is not null)
            await refreshTask;
        cts?.Dispose();
        _gpuMonitoringLease?.Dispose();
        _gpuMonitoringLease = null;
        // A rapid return to this page may already have started a new refresh.
        if (!_activity.IsActive)
        {
            UpdateValues(SensorsData.Empty);
            _readStatusText.Text = "Monitoring paused while gaming or the window is hidden.";
        }
    }

    private void Refresh()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        var token = _cts.Token;

        _refreshTask = Task.Run(async () =>
        {
            Log.Instance.Trace($"Sensors refresh started...");

            try
            {
                token.ThrowIfCancellationRequested();
                if (!await _controller.IsSupportedAsync())
                {
                    Log.Instance.Trace($"Sensors not supported.");

                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (token.IsCancellationRequested || !IsVisible) return;
                        UpdateValues(SensorsData.Empty);
                        _readStatusText.Text = "Sensor readings are unsupported or unavailable on this device.";
                    }, DispatcherPriority.Background, token);
                    return;
                }

                token.ThrowIfCancellationRequested();
                await _controller.PrepareAsync();
                token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                Log.Instance.Trace($"Sensors initialization failed.", ex);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested || !IsVisible) return;
                    UpdateValues(SensorsData.Empty);
                    _readStatusText.Text = "Sensor initialization failed. See the application log for details.";
                });
                return;
            }

            var firstSample = true;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (GamingActivityMonitor.Instance.OptionalWorkPaused) break;
                    var data = await _controller.GetDataAsync();
                    token.ThrowIfCancellationRequested();
                    if (firstSample)
                    {
                        Log.Instance.Trace($"First sensor sample: CPU={data.CPU.Temperature}C, GPU={data.GPU.Temperature}C, CPUFan={data.CPU.FanSpeed}RPM, GPUFan={data.GPU.FanSpeed}RPM.");
                        firstSample = false;
                        ReadOnlyQaSession.Mark("FirstSensorSample");
                    }
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (token.IsCancellationRequested || !IsVisible || GamingActivityMonitor.Instance.OptionalWorkPaused) return;
                        UpdateValues(data);
                        _readStatusText.Text = "Live device readings";
                    }, DispatcherPriority.Background, token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log.Instance.Trace($"Sensors refresh failed.", ex);

                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (token.IsCancellationRequested || !IsVisible) return;
                        UpdateValues(SensorsData.Empty);
                        _readStatusText.Text = "Sensor readings are temporarily unavailable.";
                    });
                }
                try { await Task.Delay(TimeSpan.FromSeconds(_dashboardSettings.Store.SensorsRefreshIntervalSeconds), token); }
                catch (OperationCanceledException) { break; }
            }

            Log.Instance.Trace($"Sensors refresh stopped.");
        });
    }

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
