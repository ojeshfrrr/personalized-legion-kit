using System;
using System.Windows;
using System.Windows.Media;

namespace LenovoLegionToolkit.WPF.Controls;

/// <summary>A passive WPF indicator. Values come from its templated progress bar.</summary>
public sealed class InstrumentDial : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(InstrumentDial), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(InstrumentDial), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(InstrumentDial), new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(InstrumentDial), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(InstrumentDial), new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var radius = Math.Min(ActualWidth, ActualHeight) / 2 - 4;
        if (radius <= 0) return;

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        DrawArc(drawingContext, center, radius, 270, Track);
        var fraction = Maximum > Minimum ? (Value - Minimum) / (Maximum - Minimum) : 0;
        if (double.IsFinite(fraction) && fraction > 0)
            DrawArc(drawingContext, center, radius, Math.Clamp(fraction, 0, 1) * 270, Stroke);
    }

    private static void DrawArc(DrawingContext context, Point center, double radius, double sweep, Brush brush)
    {
        Point At(double angle) => new(center.X + radius * Math.Cos(angle * Math.PI / 180),
            center.Y + radius * Math.Sin(angle * Math.PI / 180));
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(At(135), false, false);
            path.ArcTo(At(135 + sweep), new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        var pen = new Pen(brush, 5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        context.DrawGeometry(null, pen, geometry);
    }
}
