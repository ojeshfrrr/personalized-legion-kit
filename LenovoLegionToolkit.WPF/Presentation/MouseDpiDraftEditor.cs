using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using LenovoLegionToolkit.Lib.Services.Mouse;

namespace LenovoLegionToolkit.WPF.Presentation;

/// <summary>Local editor wiring, also exercised on detached production markup.
/// Its only callback refreshes command access; it has no device-service reference.</summary>
public sealed class MouseDpiDraftEditor
{
    private readonly MouseDpiDraft _draft = new();
    private readonly Func<bool> _canEdit;
    private readonly Action _changed;
    private readonly TextBox _x, _y;
    private readonly Slider _sx, _sy;
    private readonly ToggleButton _link;
    private readonly TextBlock _requested, _xLabel;
    private readonly FrameworkElement _yPanel;
    private bool _updating, _hasConfirmed;

    public MouseDpiDraftEditor(FrameworkElement scope, Func<bool> canEdit, Action changed)
    {
        T Find<T>(string name) where T : class => (T)scope.FindName(name);
        _canEdit = canEdit; _changed = changed;
        _x = Find<TextBox>("_dpiX"); _y = Find<TextBox>("_dpiY");
        _sx = Find<Slider>("_dpiXSlider"); _sy = Find<Slider>("_dpiYSlider");
        _link = Find<ToggleButton>("_dpiLink");
        _requested = Find<TextBlock>("_requestedDpiText");
        _xLabel = Find<TextBlock>("_dpiXSliderLabel");
        _yPanel = Find<FrameworkElement>("_dpiYSliderPanel");
        AddScale(Find<Canvas>("_dpiXScale")); AddScale(Find<Canvas>("_dpiYScale"));
        _x.TextChanged += (_, _) => TextChanged(true);
        _y.TextChanged += (_, _) => TextChanged(false);
        _sx.ValueChanged += (_, _) => PositionChanged(true);
        _sy.ValueChanged += (_, _) => PositionChanged(false);
        _link.Checked += LinkChanged; _link.Unchecked += LinkChanged;
        _sx.PreviewKeyDown += (_, e) => { if (HandleKey(true, e.Key)) e.Handled = true; };
        _sy.PreviewKeyDown += (_, e) => { if (HandleKey(false, e.Key)) e.Handled = true; };
        Render(false);
    }
    public bool TryGet(out MouseDpi? value) => _draft.TryGet(out value);
    public void SetConfirmed(MouseDpi value) { _draft.LoadConfirmed(value); _hasConfirmed = true; Render(); }
    public void UpdateAccess()
    {
        var enabled = _hasConfirmed && _canEdit();
        _x.IsEnabled = _y.IsEnabled = _sx.IsEnabled = _sy.IsEnabled = _link.IsEnabled = enabled;
    }
    private bool Editable => _hasConfirmed && _canEdit();
    private void TextChanged(bool xAxis)
    {
        if (_updating) return;
        if (Editable) _draft.SetText(xAxis, xAxis ? _x.Text : _y.Text);
        Render();
    }
    private void PositionChanged(bool xAxis)
    {
        if (_updating) return;
        if (Editable) _draft.SetPosition(xAxis, xAxis ? _sx.Value : _sy.Value);
        Render();
    }
    private void LinkChanged(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        if (Editable) _draft.SetLinked(_link.IsChecked == true);
        Render();
    }
    // Nonlinear mouse dragging, exact 50-DPI keyboard movement. No slider-position
    // increment is mistaken for a fixed DPI increment on the logarithmic scale.
    public bool HandleKey(bool xAxis, Key key)
    {
        if (!Editable || !_draft.TryAxis(xAxis, out var value)) return false;
        var dpi = key switch
        {
            Key.Left or Key.Down => MouseDpiScale.Move(value, -1),
            Key.Right or Key.Up => MouseDpiScale.Move(value, 1),
            Key.PageDown => MouseDpiScale.Move(value, -10),
            Key.PageUp => MouseDpiScale.Move(value, 10),
            Key.Home => MouseDpiScale.Minimum, Key.End => MouseDpiScale.Maximum,
            _ => -1,
        };
        if (dpi < 0) return false;
        _draft.SetText(xAxis, dpi.ToString(CultureInfo.InvariantCulture)); Render(); return true;
    }
    private void Render(bool notify = true)
    {
        _updating = true;
        try
        {
            if (_x.Text != _draft.X) _x.Text = _draft.X;
            if (_y.Text != _draft.Y) _y.Text = _draft.Y;
            if (_draft.TryAxis(true, out var x)) _sx.Value = MouseDpiScale.ToPosition(x);
            if (_draft.TryAxis(false, out var y)) _sy.Value = MouseDpiScale.ToPosition(y);
            _link.IsChecked = _draft.Linked;
            _link.Content = _draft.Linked ? "X / Y linked" : "Independent X / Y";
            _yPanel.Visibility = _draft.Linked ? Visibility.Collapsed : Visibility.Visible;
            _xLabel.Text = _draft.Linked ? "Requested X + Y" : "Requested X";
            _requested.Text = _draft.TryGet(out var dpi)
                ? _draft.Linked ? $"{dpi!.X:N0} DPI requested" : $"X {dpi!.X:N0} / Y {dpi.Y:N0} DPI requested"
                : "Requested DPI unavailable";
            AutomationProperties.SetHelpText(_sx, _draft.TryAxis(true, out x) ? $"{x} DPI requested. Arrow keys change 50 DPI." : "Enter a valid X DPI.");
            AutomationProperties.SetHelpText(_sy, _draft.TryAxis(false, out y) ? $"{y} DPI requested. Arrow keys change 50 DPI." : "Enter a valid Y DPI.");
            UpdateAccess();
        }
        finally { _updating = false; }
        if (notify) _changed();
    }
    private static void AddScale(Canvas canvas)
    {
        foreach (var (dpi, label) in new[] { (100, "100"), (400, "400"), (800, "800"), (1600, "1.6k"), (3200, "3.2k"), (6400, "6.4k"), (12000, "12k"), (30000, "30k") })
        {
            canvas.Children.Add(new Line { Tag = dpi, Y1 = 0, Y2 = 4, Stroke = new SolidColorBrush(Color.FromRgb(66, 70, 79)), IsHitTestVisible = false });
            canvas.Children.Add(new TextBlock { Tag = dpi, Text = label, FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(142, 149, 160)), IsHitTestVisible = false });
        }
        canvas.SizeChanged += (_, _) => ArrangeScale(canvas);
    }
    public static void ArrangeScale(Canvas canvas)
    {
        var width = canvas.ActualWidth;
        foreach (FrameworkElement mark in canvas.Children)
        {
            var x = 8 + MouseDpiScale.ToPosition((int)mark.Tag) * Math.Max(0, width - 16);
            if (mark is Line line) { line.X1 = line.X2 = x; }
            else
            {
                mark.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(mark, Math.Clamp(x - mark.DesiredSize.Width / 2, 0, Math.Max(0, width - mark.DesiredSize.Width)));
                Canvas.SetTop(mark, 6);
            }
        }
    }
}
