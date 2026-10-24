using System;
using System.Threading.Tasks;
using System.Windows;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Integrations;
using LenovoLegionToolkit.Lib.Settings;
using LenovoLegionToolkit.Lib.System;
using LenovoLegionToolkit.Lib.Utils;
using LenovoLegionToolkit.WPF.CLI;
using LenovoLegionToolkit.WPF.Resources;
using LenovoLegionToolkit.WPF.Utils;
using LenovoLegionToolkit.WPF.Windows.Utils;

namespace LenovoLegionToolkit.WPF.Controls.Settings;

public partial class SettingsIntegrationsControl
{
    private readonly IntegrationsSettings _integrationsSettings = IoCContainer.Resolve<IntegrationsSettings>();
    private readonly HWiNFOIntegration _hwinfoIntegration = IoCContainer.Resolve<HWiNFOIntegration>();
    private readonly IpcServer _ipcServer = IoCContainer.Resolve<IpcServer>();

    private bool _isRefreshing;

    public SettingsIntegrationsControl()
    {
        InitializeComponent();
    }

    public Task RefreshAsync()
    {
        _isRefreshing = true;

        _hwinfoIntegrationToggle.IsChecked = _hwinfoIntegration.IsRunning;
        _cliInterfaceToggle.IsChecked = _integrationsSettings.Store.CLI;
        _cliPathToggle.IsChecked = SystemPath.HasCLI();

        _hwinfoIntegrationToggle.Visibility = Visibility.Visible;
        _cliInterfaceToggle.Visibility = Visibility.Visible;
        _cliPathToggle.Visibility = Visibility.Visible;

        _isRefreshing = false;

        return Task.CompletedTask;
    }

    private async void HWiNFOIntegrationToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_isRefreshing)
            return;

        _integrationsSettings.Store.HWiNFO = _hwinfoIntegrationToggle.IsChecked ?? false;
        _integrationsSettings.SynchronizeStore();

        await _hwinfoIntegration.StartStopIfNeededAsync();
    }

    private async void CLIInterfaceToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_isRefreshing)
            return;

        _integrationsSettings.Store.CLI = _cliInterfaceToggle.IsChecked ?? false;
        _integrationsSettings.SynchronizeStore();

        await _ipcServer.StartStopIfNeededAsync();
    }

    private void CLIPathToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_isRefreshing)
            return;

        SystemPath.SetCLI(_cliPathToggle.IsChecked ?? false);
    }
}
