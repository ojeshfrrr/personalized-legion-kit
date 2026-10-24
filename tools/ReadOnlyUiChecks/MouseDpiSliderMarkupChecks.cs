using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using LenovoLegionToolkit.Lib.Services.Mouse;
using LenovoLegionToolkit.WPF.Presentation;

// Native control events on detached production markup. No IoC/page constructor,
// physical input, window, configuration, device getter or setter is involved.
internal static class MouseDpiSliderMarkupChecks
{
    public static void Exercise(Grid root, bool independent)
    {
        T Find<T>(string name) where T : class => (T)root.FindName(name);
        var editable = true;
        var changes = 0;
        var editor = new MouseDpiDraftEditor(root, () => editable, () => changes++);
        var x = Find<TextBox>("_dpiX"); var y = Find<TextBox>("_dpiY");
        var sx = Find<Slider>("_dpiXSlider"); var sy = Find<Slider>("_dpiYSlider");
        var link = Find<ToggleButton>("_dpiLink");
        Require(!sx.IsEnabled && !sy.IsEnabled && !link.IsEnabled && !editor.TryGet(out _), "No readings should invent an editable DPI draft");
        editor.SetConfirmed(new(1600, 1600)); // Explicit synthetic presentation fixture only.
        sx.Value = MouseDpiScale.ToPosition(800);
        Require(editor.TryGet(out var dpi) && dpi == new MouseDpi(800, 800) && x.Text == "800" && y.Text == "800", "Native linked slider event failed");
        y.Text = "1250";
        Require(x.Text == "1250" && editor.TryGet(out dpi) && dpi == new MouseDpi(1250, 1250), "Exact Y text did not synchronize linked X");
        link.IsChecked = false;
        sy.Value = MouseDpiScale.ToPosition(2450);
        Require(editor.TryGet(out dpi) && dpi == new MouseDpi(1250, 2450), "Independent Y slider changed X");
        Require(editor.HandleKey(true, Key.Right) && x.Text == "1300" && y.Text == "2450", "Keyboard arrow is not exactly 50 DPI");
        Require(editor.HandleKey(true, Key.PageUp) && x.Text == "1800", "Keyboard page step failed");
        Require(editor.HandleKey(true, Key.Home) && x.Text == "100" && editor.HandleKey(true, Key.End) && x.Text == "30000", "Keyboard endpoints failed");
        x.Text = "1234";
        Require(!editor.TryGet(out _) && x.Text == "1234", "Numeric input was silently rounded");
        editor.SetConfirmed(new(900, 1200));
        Require(link.IsChecked == false && x.Text == "900" && y.Text == "1200", "Reset lost asymmetric readback");
        editable = false; editor.UpdateAccess();
        Require(!sx.IsEnabled && !sy.IsEnabled && !x.IsEnabled && !y.IsEnabled && !link.IsEnabled, "Read-only access did not disable every draft input");
        sx.Value = MouseDpiScale.ToPosition(2000); x.Text = "1500"; link.IsChecked = true;
        Require(editor.TryGet(out dpi) && dpi == new MouseDpi(900, 1200) && !editor.HandleKey(true, Key.Right), "Locked control events modified draft state");
        Require(Find<TextBlock>("_currentDpiText").Text == "Unavailable", "Local draft editing fabricated confirmed hardware text");
        editable = true; editor.UpdateAccess();
        editor.SetConfirmed(new(1600, 1600)); link.IsChecked = !independent;
        if (independent) sy.Value = MouseDpiScale.ToPosition(2450);
        Require(changes > 8 && editor.TryGet(out _), "Local editor callbacks were not exercised");
        Find<TextBlock>("_statusText").Text = "Offline requested-input fixture. No hardware queried or changed.";
    }

    public static void VerifyGeometry(Grid root, bool independent)
    {
        var slider = (Slider)root.FindName("_dpiXSlider");
        var scale = (Canvas)root.FindName("_dpiXScale");
        MouseDpiDraftEditor.ArrangeScale(scale);
        var track = (Track)slider.Template.FindName("PART_Track", slider);
        Require(track.Thumb.ActualWidth == 16 && slider.ActualWidth > 100, "Native DPI track collapsed");
        var previousRight = double.NegativeInfinity;
        foreach (var label in scale.Children.OfType<TextBlock>().OrderBy(t => (int)t.Tag))
        {
            var left = Canvas.GetLeft(label);
            Require(left >= 0 && left + label.DesiredSize.Width <= scale.ActualWidth + 0.5 && left >= previousRight + 1,
                "Log-scale marker labels overlap or leave the compact card");
            previousRight = left + label.DesiredSize.Width;
        }
        foreach (var tick in scale.Children.OfType<Line>())
        {
            slider.Value = MouseDpiScale.ToPosition((int)tick.Tag);
            root.UpdateLayout();
            var thumbX = track.Thumb.TranslatePoint(new Point(track.Thumb.ActualWidth / 2, 0), root).X;
            var tickX = scale.TranslatePoint(new Point(tick.X1, 0), root).X;
            Require(Math.Abs(thumbX - tickX) < 1, "Guide mark and logarithmic thumb position are misaligned");
        }
        slider.Value = MouseDpiScale.ToPosition(1600);
        root.UpdateLayout();
        Require(((TextBox)root.FindName("_dpiX")).Text == "1600", "Slider reset and numeric requested value diverged");
        var yPanel = (FrameworkElement)root.FindName("_dpiYSliderPanel");
        Require(yPanel.Visibility == (independent ? Visibility.Visible : Visibility.Collapsed), "Independent Y slider visibility failed");
        if (independent)
        {
            var y = (Slider)root.FindName("_dpiYSlider");
            Require(y.ActualWidth > 100 && y.ActualHeight >= 24, "Independent Y slider was clipped");
        }
        var focus = (Border)slider.Template.FindName("DpiFocus", slider);
        Require(focus is not null && focus.BorderThickness.Left >= 1, "Keyboard focus treatment is absent");
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
