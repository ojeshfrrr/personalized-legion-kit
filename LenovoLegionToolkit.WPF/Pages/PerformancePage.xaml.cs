using System;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Utils;

namespace LenovoLegionToolkit.WPF.Pages;

public partial class PerformancePage
{
    public PerformancePage()
    {
        InitializeComponent();
    }

    private async void PerformancePage_Initialized(object? sender, EventArgs e)
    {
        try
        {
            var machineInformation = await Task.Run(Compatibility.GetMachineInformationAsync);
            _itsModeControl.Visibility = machineInformation.Properties.SupportsITSMode
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Performance-mode detection failed.", ex);
            _itsModeControl.Visibility = System.Windows.Visibility.Collapsed;
        }
    }
}
