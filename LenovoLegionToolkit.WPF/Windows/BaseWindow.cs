using System;
using System.Windows.Media.Imaging;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Settings;
using LenovoLegionToolkit.WPF.Utils;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace LenovoLegionToolkit.WPF.Windows;

public class BaseWindow : UiWindow
{
    private static readonly ImageSource ApplicationIcon = LoadApplicationIcon();

    private static ImageSource LoadApplicationIcon()
    {
        var image = BitmapFrame.Create(new Uri("pack://application:,,,/Project%20Legion;component/Assets/ProjectLegion.ico", UriKind.Absolute));
        image.Freeze();
        return image;
    }

    protected BaseWindow()
    {
        Icon = ApplicationIcon;
        SnapsToDevicePixels = true;
        ExtendsContentIntoTitleBar = true;

        var settings = IoCContainer.Resolve<ApplicationSettings>();
        WindowBackdropType = ThemeManager.GetBackgroundType(settings.Store.BackdropType);

        DpiChanged += BaseWindow_DpiChanged;

        PreviewKeyDown += (s, e) => {
            if (e.Key == Key.System && e.SystemKey == Key.LeftAlt)
            {
                e.Handled = true;
                Keyboard.ClearFocus();
            }
        };  
    }

    private void BaseWindow_DpiChanged(object sender, DpiChangedEventArgs e) => VisualTreeHelper.SetRootDpi(this, e.NewDpi);
}
