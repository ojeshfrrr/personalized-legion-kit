using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using LenovoLegionToolkit.Lib.Utils;
using Newtonsoft.Json;

namespace LenovoLegionToolkit.WPF.Utils;

// Opt-in lifecycle observations only; never enables commands or changes telemetry cadence.
internal static class ReadOnlyQaSession
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, double> Stages = [];
    private static Stopwatch? _clock;
    public static void Start(Stopwatch clock)
    {
        if (AppFlags.Instance.QaExitAfterSeconds == 0) return;
        _clock = clock;
        Mark("CoreInitialized");
    }
    public static void Mark(string stage)
    {
        if (_clock is null) return;
        lock (Gate)
        {
            if (!Stages.TryAdd(stage, _clock.Elapsed.TotalMilliseconds)) return;
            File.WriteAllText(Path.Combine(Folders.AppData, "qa-session.json"), JsonConvert.SerializeObject(new
            {
                ProcessId = Environment.ProcessId, ProcessStartUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime(),
                Trace = Log.Instance.IsTraceEnabled, Minimized = AppFlags.Instance.Minimized,
                ForegroundVisibilityVerified = false, Stages
            }, Formatting.Indented));
        }
    }
}
