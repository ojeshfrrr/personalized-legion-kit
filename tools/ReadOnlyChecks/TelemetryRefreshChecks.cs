using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Utils;

internal static class TelemetryRefreshChecks
{
    private sealed class Ticks
    {
        private readonly SemaphoreSlim _tick = new(0, 1);
        public int Waiters, PeakWaiters, Calls;
        public async Task WaitAsync(CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            PeakWaiters = Math.Max(PeakWaiters, Interlocked.Increment(ref Waiters));
            try { await _tick.WaitAsync(token); }
            finally { Interlocked.Decrement(ref Waiters); }
        }
        public void Pulse() { if (_tick.CurrentCount == 0) _tick.Release(); }
        public void Clear() { while (_tick.Wait(0)) { } }
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task UntilAsync(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(5, deadline.Token);
    }
    public static async Task RunAsync(Action<bool, string> check)
    {
        var ticks = new Ticks(); var reads = 0; var publishes = 0; var writes = 0;
        var active = 0; var peak = 0; var blocked = false;
        var started = Signal(); var release = Signal();
        var session = new DemandReadSession<int>(async token =>
        {
            var value = Interlocked.Increment(ref reads);
            peak = Math.Max(peak, Interlocked.Increment(ref active));
            try { if (blocked) { started.TrySetResult(); await release.Task; } return value; }
            finally { Interlocked.Decrement(ref active); }
        }, (_, _) => Interlocked.Increment(ref publishes), _ => throw new InvalidOperationException("Unexpected fake-provider error"),
            TimeSpan.FromSeconds(1), ticks.WaitAsync);
        check(reads == 0 && ticks.Calls == 0, "telemetry construction starts neither timer nor device query");
        await session.ActivateAsync(); await UntilAsync(() => ticks.Waiters == 1);
        check(reads == 1 && publishes == 1, "foreground page entry reads immediately before its first scheduled tick");
        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => session.ActivateAsync()));
        check(reads == 1 && ticks.PeakWaiters == 1, "duplicate activations keep one timer and one initial read");
        ticks.Pulse(); await UntilAsync(() => publishes == 2 && ticks.Waiters == 1);
        check(reads == 2, "one active telemetry tick produces one fresh getter result");
        blocked = true; ticks.Pulse(); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var requests = Enumerable.Range(0, 50).Select(_ => session.RefreshAsync()).ToArray();
        check(requests.All(t => ReferenceEquals(t, requests[0])) && reads == 3,
            "manual and scheduled requests coalesce behind the same in-flight read");
        session.InvalidatePendingRead();
        check(reads == 3, "changing a presentation selection cancels stale output without issuing another query");
        var stop = session.DeactivateAsync();
        check(!stop.IsCompleted, "navigation/minimize retirement drains an uncancellable getter");
        release.TrySetResult(); await stop.WaitAsync(TimeSpan.FromSeconds(5));
        check(publishes == 2 && reads == 3 && ticks.Waiters == 0,
            "navigation/minimize stops the timer and suppresses the late sensor sample");
        ticks.Pulse(); await Task.Delay(30);
        check(reads == 3 && writes == 0, "inactive/minimized timer ticks cause neither reads nor setters");
        ticks.Clear(); blocked = false;
        await session.ActivateAsync(); await UntilAsync(() => ticks.Waiters == 1);
        check(reads == 4 && publishes == 3, "foreground restoration takes one immediate fresh reading");
        ticks.Pulse(); await UntilAsync(() => publishes == 4);
        await session.DeactivateAsync();
        check(peak == 1 && active == 0 && ticks.PeakWaiters == 1,
            "repeated stop/resume transitions never overlap getters or duplicate timers");

        var otherTicks = new Ticks(); var batteryReads = 0;
        var battery = new DemandReadSession<int>(_ => Task.FromResult(++batteryReads), (_, _) => { }, _ => { },
            TimeSpan.FromSeconds(5), otherTicks.WaitAsync);
        await battery.ActivateAsync(); await UntilAsync(() => otherTicks.Waiters == 1);
        ticks.Pulse(); otherTicks.Pulse(); await UntilAsync(() => batteryReads == 2);
        check(reads == 5 && batteryReads == 2, "navigation refreshes the newly active owner while the former page stays stopped");
        await battery.DeactivateAsync();
        otherTicks.Pulse(); await Task.Delay(30);
        check(batteryReads == 2 && otherTicks.Waiters == 0, "focus/game/minimize deactivation leaves no Battery timer waiting");

        // Exercise the actual PeriodicTimer path too, with a short test-only interval.
        var realReads = 0;
        var real = new DemandReadSession<int>(_ => Task.FromResult(Interlocked.Increment(ref realReads)), (_, _) => { }, _ => { }, TimeSpan.FromMilliseconds(50));
        await real.ActivateAsync(); await UntilAsync(() => realReads >= 3);
        await real.DeactivateAsync(); var settled = realReads; await Task.Delay(120);
        check(settled >= 3 && realReads == settled && !real.IsBusy, "actual PeriodicTimer refreshes and cancels without leaving a worker");
        try
        {
            _ = new DemandReadSession<int>(_ => Task.FromResult(0), (_, _) => { }, _ => { }, TimeSpan.Zero);
            check(false, "invalid cadence must fail");
        }
        catch (ArgumentOutOfRangeException) { check(true, "zero cadence is rejected instead of creating a busy loop"); }

        string Source(string path) => File.ReadAllText(Path.Combine(Environment.CurrentDirectory, path));
        var sensors = Source("LenovoLegionToolkit.WPF/Controls/Dashboard/SensorsControl.xaml.cs");
        var advanced = Source("LenovoLegionToolkit.WPF/Controls/Dashboard/SensorsControlV2.xaml.cs");
        var batterySource = Source("LenovoLegionToolkit.WPF/Pages/BatteryPage.xaml.cs");
        check(sensors.Contains("refreshInterval: TimeSpan.FromSeconds(1)") && advanced.Contains("refreshInterval: TimeSpan.FromSeconds(1)"),
            "Home/Thermals sensor owners use the explicit 1000 ms cadence");
        check(new[] { sensors, advanced }.All(source => source.Contains("}, suspendForGaming: false)") && !source.Contains("requireForeground: false")),
            "Home/Thermals permit gaming-time sensor reads without opting out of the foreground gate");
        check(batterySource.Contains("refreshInterval: TimeSpan.FromSeconds(5)"), "Battery uses the explicit 5000 ms cadence");
        check(sensors.Contains("if (!_prepared)") && sensors.Contains("_prepared = false"), "CPU sampling baseline is initialized once per activation, not every tick");
        foreach (var source in new[] { sensors, advanced, batterySource })
        {
            var getter = source[source.IndexOf("        _reads = ")..source.IndexOf("        _activity = ")];
            check(!new[] { ".Apply", ".SetState", ".SetDpi", ".SetPolling", ".SetPState", ".SynchronizeStore" }.Any(getter.Contains),
                "telemetry timer callback has no hardware setter or settings-persistence call");
        }
        var activity = Source("LenovoLegionToolkit.WPF/Utils/OptionalUiActivity.cs");
        check(activity.Contains("_window.Activated +=") && activity.Contains("_window.Deactivated +=") &&
            activity.Contains("_window.Activated -=") && activity.Contains("_window.Deactivated -=") && activity.Contains("_window.IsActive"),
            "foreground listeners attach/detach and gate optional telemetry by actual window activation");
        var mouse = Source("LenovoLegionToolkit.WPF/Pages/MousePage.xaml.cs");
        check(mouse.Contains("await _buttons.ReadProfilesAsync()") && mouse.Contains("await _buttons.ReadAssignmentsAsync()") && !mouse.Contains("refreshInterval:"),
            "Mouse entry reads bank assignments as well as DPI/polling, with no periodic cadence");
        check(mouse.Contains("requireForeground: false, suspendForGaming: false"), "one-shot Mouse operations are not cancelled by confirmation focus or game-presence changes");
        foreach (var file in new[] { "LenovoLegionToolkit.WPF/Controls/Dashboard/DiscreteGPUControl.xaml.cs", "LenovoLegionToolkit.WPF/Windows/Dashboard/DiscreteGPUManagementWindow.xaml.cs" })
        {
            check(!Source(file).Contains("refreshInterval:"), "Graphics status/app enumeration has no periodic UI cadence");
            check(!Source(file).Contains("suspendForGaming: false"), "expensive GPU status/process enumeration retains gaming suspension");
        }
    }
}
