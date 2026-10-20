using System.Windows;
using LenovoLegionToolkit.WPF.Extensions;

namespace LenovoLegionToolkit.WPF.Pages;

public partial class DonatePage
{
    public DonatePage() => InitializeComponent();

    private void OriginalSource_Click(object sender, RoutedEventArgs e)
    {
        Constants.OriginalProjectUri.Open();
        e.Handled = true;
    }
}
