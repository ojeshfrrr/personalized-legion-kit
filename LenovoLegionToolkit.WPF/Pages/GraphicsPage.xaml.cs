using System;
using System.Threading.Tasks;
using LenovoLegionToolkit.WPF.Controls.Dashboard;
using LenovoLegionToolkit.WPF.Extensions;
using LenovoLegionToolkit.WPF.Resources;
using LenovoLegionToolkit.Lib.Utils;

namespace LenovoLegionToolkit.WPF.Pages;

public partial class GraphicsPage
{
    public GraphicsPage()
    {
        InitializeComponent();
    }

    private async void GraphicsPage_Initialized(object? sender, EventArgs e)
    {
        try
        {
            _modeControls.Children.Add(await HybridModeControlFactory.GetControlAsync(emphasizeSelection: true));
            _controls.Children.Add(new DiscreteGPUControl());
            _controls.Children.Add(new OverclockDiscreteGPUControl());
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Couldn't initialize graphics controls.", ex);
            _errorText.Text = ex.Message;
            _errorText.Visibility = System.Windows.Visibility.Visible;
        }
        finally
        {
            _loadingIndicator.Visibility = System.Windows.Visibility.Collapsed;
        }
    }
}
