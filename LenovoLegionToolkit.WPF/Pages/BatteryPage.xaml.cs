using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Humanizer;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Settings;
using LenovoLegionToolkit.Lib.System;
using LenovoLegionToolkit.Lib.Utils;
using LenovoLegionToolkit.WPF.Resources;
using LenovoLegionToolkit.WPF.Utils;
using Wpf.Ui.Common;

namespace LenovoLegionToolkit.WPF.Pages;

public partial class BatteryPage
{
    private readonly ApplicationSettings _settings = IoCContainer.Resolve<ApplicationSettings>();

    private CancellationTokenSource? _cts;
    private Task? _refreshTask;
    private readonly OptionalUiActivity _activity;

    public BatteryPage()
    {
        InitializeComponent();

        _activity = new OptionalUiActivity(this, SetActivityAsync);
    }

    private async Task SetActivityAsync(bool active)
    {
        if (active)
        {
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
    }

    private void Refresh()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        var token = _cts.Token;

        _refreshTask = Task.Run(async () =>
        {
            Log.Instance.Trace($"Battery information refresh started...");

            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (GamingActivityMonitor.Instance.OptionalWorkPaused) break;
                    var batteryInfo = Battery.GetBatteryInformation();
                    token.ThrowIfCancellationRequested();
                    var powerAdapterStatus = await Power.IsPowerAdapterConnectedAsync();
                    token.ThrowIfCancellationRequested();
                    var onBatterySince = Battery.GetOnBatterySince();
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!token.IsCancellationRequested && IsVisible && !GamingActivityMonitor.Instance.OptionalWorkPaused)
                            Set(batteryInfo, powerAdapterStatus, onBatterySince);
                    }, DispatcherPriority.Background, token);

                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log.Instance.Trace($"Battery information refresh failed.", ex);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (token.IsCancellationRequested || !IsVisible) return;
                        _percentRemaining.Text = "—";
                        _batteryChargeBar.Value = 0;
                        _status.Text = "Battery information is temporarily unavailable.";
                    });
                }
                try { await Task.Delay(TimeSpan.FromSeconds(2), token); }
                catch (OperationCanceledException) { break; }
            }

            Log.Instance.Trace($"Battery information refresh stopped.");
        });
    }

    private void Set(BatteryInformation batteryInfo, PowerAdapterStatus powerAdapterStatus, DateTime? onBatterySince)
    {
        var number = (int)Math.Round(batteryInfo.BatteryPercentage / 10.0);
        _batteryIcon.Symbol = number switch
        {
            10 => SymbolRegular.Battery1024,
            9 => SymbolRegular.Battery924,
            8 => SymbolRegular.Battery824,
            7 => SymbolRegular.Battery724,
            6 => SymbolRegular.Battery624,
            5 => SymbolRegular.Battery524,
            4 => SymbolRegular.Battery424,
            3 => SymbolRegular.Battery324,
            2 => SymbolRegular.Battery224,
            1 => SymbolRegular.Battery124,
            _ => SymbolRegular.Battery024,
        };

        _percentRemaining.Text = $"{batteryInfo.BatteryPercentage}{Resource.Percent}";
        _batteryChargeBar.Value = Math.Clamp((double)batteryInfo.BatteryPercentage, 0, 100);
        _status.Text = GetStatusText(batteryInfo);
        _lowBattery.Visibility = batteryInfo.IsLowBattery ? Visibility.Visible : Visibility.Collapsed;
        _lowWattageCharger.Visibility = powerAdapterStatus == PowerAdapterStatus.ConnectedLowWattage ? Visibility.Visible : Visibility.Collapsed;

        if (batteryInfo.BatteryTemperatureC is not null)
            _batteryTemperatureText.Text = GetTemperatureText(batteryInfo.BatteryTemperatureC);
        else
            _batteryTemperatureCardControl.Visibility = Visibility.Collapsed;

        if (!batteryInfo.IsCharging && onBatterySince.HasValue)
        {
            var onBatterySinceValue = onBatterySince.Value;
            var dateText = onBatterySinceValue.ToString("G", Resource.Culture);
            var duration = DateTime.Now.Subtract(onBatterySinceValue);
            _onBatterySinceText.Text = $"{dateText} ({duration.Humanize(2, Resource.Culture, minUnit: TimeUnit.Second)})";
        }
        else
        {
            _onBatterySinceText.Text = "-";
        }

        _batteryDischargeRateText.Text = $"{batteryInfo.DischargeRate / 1000.0:+0.00;-0.00;0.00} {Resource.Watt}";
        _batteryMinDischargeRateText.Text = $"{batteryInfo.MinDischargeRate / 1000.0:+0.00;-0.00;0.00} {Resource.Watt}";
        _batteryMaxDischargeRateText.Text = $"{batteryInfo.MaxDischargeRate / 1000.0:+0.00;-0.00;0.00} {Resource.Watt}";
        _batteryCapacityText.Text = $"{batteryInfo.EstimateChargeRemaining / 1000.0:0.00} Wh";
        _batteryFullChargeCapacityText.Text = $"{batteryInfo.FullChargeCapacity / 1000.0:0.00} Wh";
        _batteryDesignCapacityText.Text = $"{batteryInfo.DesignCapacity / 1000.0:0.00} Wh";
        _batteryHealthText.Text = $"{batteryInfo.BatteryHealth:0.00} {Resource.Percent}";

        if (batteryInfo.ManufactureDate is not null)
            _batteryManufactureDateText.Text = batteryInfo.ManufactureDate?.ToString(LocalizationHelper.ShortDateFormat) ?? "-";
        else
            _batteryManufactureDateCardControl.Visibility = Visibility.Collapsed;

        if (batteryInfo.FirstUseDate is not null)
            _batteryFirstUseDateText.Text = batteryInfo.FirstUseDate?.ToString(LocalizationHelper.ShortDateFormat) ?? "-";
        else
            _batteryFirstUseDateCardControl.Visibility = Visibility.Collapsed;

        _batteryCycleCountText.Text = $"{batteryInfo.CycleCount}";
    }

    private static string GetStatusText(BatteryInformation batteryInfo)
    {
        if (batteryInfo.IsCharging)
        {
            if (batteryInfo.DischargeRate > 0)
                return Resource.BatteryPage_ACAdapterConnectedAndCharging;

            return Resource.BatteryPage_ACAdapterConnectedNotCharging;
        }

        if (batteryInfo.BatteryLifeRemaining < 0)
            return Resource.BatteryPage_EstimatingBatteryLife;

        var time = TimeSpan.FromSeconds(batteryInfo.BatteryLifeRemaining).Humanize(2, Resource.Culture);
        return string.Format(Resource.BatteryPage_EstimatedBatteryLifeRemaining, time);
    }

    private string GetTemperatureText(double? temperature)
    {
        _batteryTemperatureCardControl.Tag = temperature;

        if (temperature is null || double.IsNaN(temperature.Value) || temperature.Value < 0)
            return "—";

        if (_settings.Store.TemperatureUnit == TemperatureUnit.F)
        {
            var fahrenheit = temperature.Value * 9.0 / 5.0 + 32.0;
            return $"{fahrenheit:0.0} {Resource.Fahrenheit}";
        }

        return $"{temperature:0.0} {Resource.Celsius}";
    }
}
