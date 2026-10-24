using System;
using System.Globalization;
using LenovoLegionToolkit.Lib.Services.Mouse;

namespace LenovoLegionToolkit.WPF.Presentation;

/// <summary>Presentation-only DPI scale. No transport, settings, timer or device state.</summary>
public static class MouseDpiScale
{
    public const int Minimum = DeathAdderV3Protocol.MinimumDpi;
    public const int Maximum = DeathAdderV3Protocol.MaximumDpi;
    public const int Step = DeathAdderV3Protocol.DpiStep;
    private static readonly double LogRange = Math.Log((double)Maximum / Minimum);

    public static double ToPosition(int dpi)
    {
        if (!IsValid(dpi)) throw new ArgumentOutOfRangeException(nameof(dpi));
        return Math.Log((double)dpi / Minimum) / LogRange;
    }
    public static int FromPosition(double position)
    {
        if (!double.IsFinite(position)) throw new ArgumentOutOfRangeException(nameof(position));
        var dpi = Minimum * Math.Exp(Math.Clamp(position, 0, 1) * LogRange);
        return Math.Clamp((int)Math.Round(dpi / Step, MidpointRounding.AwayFromZero) * Step, Minimum, Maximum);
    }
    public static bool IsValid(int dpi) => dpi >= Minimum && dpi <= Maximum && dpi % Step == 0;
    public static int Move(int dpi, int steps) => (int)Math.Clamp((long)dpi + (long)steps * Step, Minimum, Maximum);
}

/// <summary>Requested values only. Changing a draft never confirms or applies it.</summary>
public sealed class MouseDpiDraft
{
    public string X { get; private set; } = "";
    public string Y { get; private set; } = "";
    public bool Linked { get; private set; } = true;
    public bool TryAxis(bool xAxis, out int value) => int.TryParse(xAxis ? X : Y,
        NumberStyles.None, CultureInfo.InvariantCulture, out value) && MouseDpiScale.IsValid(value);
    public bool TryGet(out MouseDpi? value)
    {
        value = null;
        if (!TryAxis(true, out var x) || !TryAxis(false, out var y)) return false;
        value = new((ushort)x, (ushort)y);
        return true;
    }
    public void LoadConfirmed(MouseDpi value)
    {
        // Never force an asymmetric real reading through a linked text-change handler.
        // Also preserve an unusual reading verbatim. It must not be silently
        // rounded into valid intent merely to fit the slider's verified range.
        X = value.X.ToString(CultureInfo.InvariantCulture);
        Y = value.Y.ToString(CultureInfo.InvariantCulture);
        if (value.X != value.Y) Linked = false;
    }
    public void SetText(bool xAxis, string text)
    {
        if (xAxis) X = text; else Y = text;
        // Keep incomplete/invalid typing visible; do not round it into valid intent.
        if (Linked && TryAxis(xAxis, out _))
        {
            if (xAxis) Y = text; else X = text;
        }
    }
    public void SetPosition(bool xAxis, double position) =>
        SetText(xAxis, MouseDpiScale.FromPosition(position).ToString(CultureInfo.InvariantCulture));
    public void SetLinked(bool linked)
    {
        Linked = linked;
        // Linking uses valid X, or valid Y if X is incomplete. No device operation.
        if (!linked) return;
        if (TryAxis(true, out _)) Y = X;
        else if (TryAxis(false, out _)) X = Y;
    }
}
