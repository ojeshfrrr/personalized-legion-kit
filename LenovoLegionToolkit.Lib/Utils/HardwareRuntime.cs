using System;
using System.Threading;

namespace LenovoLegionToolkit.Lib.Utils;

/// <summary>Session permission for the desktop application's existing hardware commands.</summary>
public static class HardwareRuntime
{
    private static int _controlsEnabled;

    public static bool ControlsEnabled => Volatile.Read(ref _controlsEnabled) != 0;

    public static void EnableControls()
    {
        if (AppFlags.Instance.UiPreview || AppFlags.Instance.ReadOnly)
            throw new InvalidOperationException("Hardware controls are locked for this diagnostic session.");

        Interlocked.Exchange(ref _controlsEnabled, 1);
    }

    public static void RequireControls()
    {
        if (!ControlsEnabled)
            throw new InvalidOperationException("Enable hardware controls for this session before changing settings.");
    }
}
