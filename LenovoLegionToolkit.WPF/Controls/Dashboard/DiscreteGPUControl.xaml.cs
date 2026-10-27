using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Controllers;
using LenovoLegionToolkit.Lib.Utils;
using LenovoLegionToolkit.WPF.Resources;
using LenovoLegionToolkit.WPF.Utils;
using LenovoLegionToolkit.WPF.Windows.Dashboard;

namespace LenovoLegionToolkit.WPF.Controls.Dashboard;

public partial class DiscreteGPUControl
{
    private readonly GPUController _gpuController = IoCContainer.Resolve<GPUController>();
    private readonly OptionalUiActivity _activity;
    private readonly DemandReadSession<GPUStatus> _reads;

    public DiscreteGPUControl()
    {
        InitializeComponent();
        ContextMenu = new System.Windows.Controls.ContextMenu();
        var refresh = new Wpf.Ui.Controls.MenuItem { Header = "Refresh GPU status", Tag = "ReadOnlyRefresh" };
        refresh.Click += ReadRefresh_Click;
        ContextMenu.Items.Add(refresh);
        _reads = new DemandReadSession<GPUStatus>(token => _gpuController.RefreshNowAsync(token), (status, time) =>
        {
            ApplyGpuStatus(status);
            _gpuReadStatus.Text = $"Read {time:HH:mm:ss} · snapshot";
        }, ex =>
        {
            _gpuReadStatus.Text = "GPU status unavailable · Refresh to try again";
            Log.Instance.Trace($"GPU snapshot unavailable.", ex);
        });
        _activity = new OptionalUiActivity(this, active =>
        {
            if (active) _ = _reads.ActivateAsync();
            else _ = _reads.DeactivateAsync();
            return Task.CompletedTask;
        });
        _activity.StateChanged += state => _gpuReadStatus.Text = OptionalUiPolicy.Describe(state);
    }

    protected override void OnFinishedLoading() { }

    protected override Task OnRefreshAsync() => _reads.RefreshAsync();
    private async void ReadRefresh_Click(object sender, RoutedEventArgs e) => await _reads.RefreshAsync();

    private void ApplyGpuStatus(GPUStatus e) => Dispatcher.Invoke(() =>
    {
        if (!_activity.IsActive || GamingActivityMonitor.Instance.OptionalWorkPaused)
            return;

        var tooltipStringBuilder = new StringBuilder(Resource.DiscreteGPUControl_PerformanceState);
        tooltipStringBuilder.AppendLine().Append(" · ").Append(e.PerformanceState ?? Resource.DiscreteGPUControl_PerformanceState_Unknown);

        if (e.State is GPUState.Unknown or GPUState.NvidiaGpuNotFound)
        {
            _discreteGPUStatusActiveIndicator.Visibility = Visibility.Collapsed;
            _discreteGPUStatusInactiveIndicator.Visibility = Visibility.Collapsed;
            _discreteGPUStatusDescription.Text = "-";
            _gpuInfoButton.ToolTip = null;
            _gpuInfoButton.IsEnabled = false;
            _deactivateGPUButton.IsEnabled = false;
            Visibility = Visibility.Visible;
            return;
        }

        if (e.State is GPUState.MonitorConnected)
            tooltipStringBuilder.AppendLine().AppendLine().Append(Resource.DiscreteGPUControl_MonitorConnected);

        if (e.State is GPUState.Active or GPUState.MonitorConnected)
        {
            var processesStringBuilder = new StringBuilder();

            if (e.ProcessCount > 0)
            {
                processesStringBuilder.Append(Resource.DiscreteGPUControl_Processes);
                foreach (var p in e.Processes.OrderBy(p => p.ProcessName))
                {
                    try { processesStringBuilder.AppendLine().Append(" · ").Append(p.ProcessName); }
                    catch {  /* Ignore */ }
                }
            }
            else
            {
                processesStringBuilder.Append(Resource.DiscreteGPUControl_NoProcesses);
            }

            _discreteGPUStatusActiveIndicator.Visibility = Visibility.Visible;
            _discreteGPUStatusInactiveIndicator.Visibility = Visibility.Collapsed;
            _discreteGPUStatusPoweredOffIndicator.Visibility = Visibility.Collapsed;
            _discreteGPUStatusDescription.Text = _gpuController.CurrentPerformanceState is { } pState
                ? $"{Resource.Active} (P{(uint)pState})"
                : Resource.Active;
            _gpuInfoButton.ToolTip = tooltipStringBuilder.AppendLine().AppendLine().Append(processesStringBuilder).ToString();
            _gpuInfoButton.IsEnabled = true;
        }
        else if (e.State is GPUState.PoweredOff)
        {
            _discreteGPUStatusActiveIndicator.Visibility = Visibility.Collapsed;
            _discreteGPUStatusInactiveIndicator.Visibility = Visibility.Collapsed;
            _discreteGPUStatusPoweredOffIndicator.Visibility = Visibility.Visible;
            _discreteGPUStatusDescription.Text = Resource.PoweredOff;
            _gpuInfoButton.ToolTip = tooltipStringBuilder.ToString();
            _gpuInfoButton.IsEnabled = true;
        }
        else
        {
            _discreteGPUStatusActiveIndicator.Visibility = Visibility.Collapsed;
            _discreteGPUStatusInactiveIndicator.Visibility = Visibility.Visible;
            _discreteGPUStatusPoweredOffIndicator.Visibility = Visibility.Collapsed;
            _discreteGPUStatusDescription.Text = _gpuController.CurrentPerformanceState is { } pState
                ? $"{Resource.Inactive} (P{(uint)pState})"
                : Resource.Inactive;
            if (!string.IsNullOrEmpty(e.SleepBlockerReason))
            {
                tooltipStringBuilder.AppendLine().AppendLine().Append(e.SleepBlockerReason);
            }
            _gpuInfoButton.ToolTip = tooltipStringBuilder.ToString();
            _gpuInfoButton.IsEnabled = true;
        }

        _deactivateGPUButton.IsEnabled = e.State is GPUState.Active or GPUState.Inactive;
        _killAppsMenuItem.IsEnabled = e.State is GPUState.Active;
        _restartGPUMenuItem.IsEnabled = e.State is GPUState.Active or GPUState.Inactive;

        if (e.State is GPUState.Active or GPUState.Inactive)
        {
            _deactivateGPUButtonText.SetResourceReference(ForegroundProperty, "TextOnAccentFillColorPrimaryBrush");
            _deactivateGPUButtonIcon.SetResourceReference(ForegroundProperty, "TextOnAccentFillColorPrimaryBrush");
        }
        else
        {
            _deactivateGPUButtonText.SetResourceReference(ForegroundProperty, "TextFillColorDisabledBrush");
            _deactivateGPUButtonIcon.SetResourceReference(ForegroundProperty, "TextFillColorDisabledBrush");
        }

        _content.Visibility = Visibility.Visible;
    });

    private void DeactivateGPUButton_Click(object sender, RoutedEventArgs e)
    {
        if (_deactivateGPUButton.ContextMenu is null)
            return;

        _deactivateGPUButton.ContextMenu.PlacementTarget = _deactivateGPUButton;
        _deactivateGPUButton.ContextMenu.Placement = PlacementMode.Bottom;
        _deactivateGPUButton.ContextMenu.IsOpen = true;
    }

    private async void KillAppsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _deactivateGPUButton.IsEnabled = false;
        await _gpuController.KillGPUProcessesAsync();
        await _reads.RefreshAsync();
    }

    private async void RestartGPUMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _deactivateGPUButton.IsEnabled = false;
        await _gpuController.RestartGPUAsync();
        await _reads.RefreshAsync();
    }

    private void GpuInfoButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new DiscreteGPUManagementWindow
        {
            Owner = Window.GetWindow(this)
        };
        window.ShowDialog();
    }
}
