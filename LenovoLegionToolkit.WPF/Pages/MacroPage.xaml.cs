using System;
using System.Linq;
using System.Windows;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Extensions;
using LenovoLegionToolkit.Lib.Macro;
using LenovoLegionToolkit.Lib.Utils;
using Wpf.Ui.Common;
using Wpf.Ui.Controls;

namespace LenovoLegionToolkit.WPF.Pages;

public partial class MacroPage
{
    private readonly MacroController _controller = IoCContainer.Resolve<MacroController>();

    private bool _subscribed;

    public MacroPage()
    {
        Initialized += MacroPage_Initialized;

        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (_subscribed) return;
            _subscribed = true;
            _controller.StateChanged += Controller_StateChanged;
            _enableMacroToggle.IsChecked = _controller.IsEnabled && _controller.IsListening;
        };
        Unloaded += (_, _) =>
        {
            _subscribed = false;
            _controller.StateChanged -= Controller_StateChanged;
        };
    }

    private void Controller_StateChanged(object? sender, EventArgs e)
    {
        if (!_subscribed || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_subscribed) _enableMacroToggle.IsChecked = _controller.IsEnabled && _controller.IsListening;
        }));
    }

    private void MacroPage_Initialized(object? sender, EventArgs e)
    {

        _enableMacroToggle.IsChecked = _controller.IsEnabled && _controller.IsListening;

        var zeroNumberButton = _numberPad.Children.OfType<Button>().Last();
        Reload(zeroNumberButton);
    }

    private void EnableMacroToggle_Click(object sender, RoutedEventArgs e)
    {
        HardwareRuntime.RequireControls();
        if (_enableMacroToggle.IsChecked == true)
            _controller.Start();
        else
            _controller.Stop();
        _controller.SetEnabled(_enableMacroToggle.IsChecked ?? false);
    }

    private void NumberPadButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
            return;

        _numberPad.Children
            .OfType<Button>()
            .ForEach(b => b.Appearance = ControlAppearance.Secondary);

        Reload(button);
    }

    private void Reload(Button button)
    {
        button.Appearance = ControlAppearance.Primary;

        var key = Convert.ToUInt64((string)button.Tag, 16);
        _sequenceControl.Set(new(MacroSource.Keyboard, key));
    }
}
