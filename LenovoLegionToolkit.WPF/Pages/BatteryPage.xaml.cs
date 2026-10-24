using System;
using System.Threading.Tasks;
using System.Windows;
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

    private readonly DemandReadSession<(BatteryInformation Battery, PowerAdapterStatus Adapter, DateTime? Since)> _reads;
    private readonly OptionalUiActivity _activity;

    public BatteryPage()
    {
        InitializeComponent();
        _reads = new(async token =>
        {
            token.ThrowIfCancellationRequested();
            var battery = Battery.GetBatteryInformation();
            token.ThrowIfCancellationRequested();
            var adapter = await Power.IsPowerAdapterConnectedAsync();
            token.ThrowIfCancellationRequested();
            var since = Battery.GetOnBatterySince();
            token.ThrowIfCancellationRequested();
            return (battery, adapter, since);
        }, (data, time) =>
        {
            Set(data.Battery, data.Adapter, data.Since);
            _batteryReadStatus.Text = $"Read {time:HH:mm:ss}";
        }, ex =>
        {
            _batteryReadStatus.Text = "Reading unavailable";
            Log.Instance.Trace($"Battery snapshot unavailable.", ex);
        }, refreshInterval: TimeSpan.FromSeconds(5));
        _activity = new OptionalUiActivity(this, active =>
        {
            if (active) _ = _reads.ActivateAsync();
            else _ = _reads.DeactivateAsync();
            return Task.CompletedTask;
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
