using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Controllers;
using LenovoLegionToolkit.Lib.Utils;

internal static class GamingWorkloadChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var present = 0;
        var failDetection = 0;
        var detections = 0;
        var monitor = new GamingActivityMonitor(() =>
        {
            Interlocked.Increment(ref detections);
            if (Volatile.Read(ref failDetection) != 0) throw new InvalidOperationException("Synthetic detection failure");
            return Volatile.Read(ref present) != 0;
        }, TimeSpan.FromMilliseconds(15));
        var gpu = new OptionalGpuProbe(monitor);
        IDisposable? optional = null;
        IDisposable? required = null;
        try
        {
            monitor.SetWindowVisible(true);
            await Task.WhenAll(Enumerable.Range(0, 25).Select(_ => monitor.StartAsync()));
            await UntilAsync(() => !monitor.OptionalWorkPaused);
            check(!monitor.GameRunning, "fake process source detects game absence without inspecting Windows processes");

            await gpu.StartAsync(delay: 0);
            await Task.Delay(50);
            check(gpu.Reads == 0, "GPU worker does no optional queries without a UI owner");
            optional = gpu.AcquireOptionalMonitoring();
            await UntilAsync(() => gpu.Reads > 0);
            check(gpu.Starts == 1, "optional monitoring resumes the existing worker without reinitialization");

            Volatile.Write(ref present, 1);
            await UntilAsync(() => monitor.GameRunning && monitor.OptionalWorkPaused);
            await AssertQuietAsync(gpu, check, "game start suspends optional GPU polling");

            using (var cancellation = new CancellationTokenSource())
            {
                var pausedWait = new OptionalWorkGate().WaitAsync(cancellation.Token);
                cancellation.Cancel();
                try { await pausedWait; }
                catch (OperationCanceledException) { }
                check(pausedWait.IsCanceled, "paused optional work cancels without polling or waiting for game exit");
            }

            var pausedReads = gpu.Reads;
            required = gpu.AcquireRequiredMonitoring();
            await UntilAsync(() => gpu.Reads > pausedReads);
            check(monitor.GameRunning, "an active required monitoring owner continues while the game is running");
            required.Dispose();
            required.Dispose(); // Releasing a lease twice cannot underflow ownership.
            required = null;
            await AssertQuietAsync(gpu, check, "releasing required monitoring restores the gaming pause");

            monitor.SetWindowVisible(false);
            Volatile.Write(ref present, 0);
            await UntilAsync(() => !monitor.GameRunning);
            await AssertQuietAsync(gpu, check, "game exit does not resume hidden-window telemetry");
            check(monitor.OptionalWorkPaused, "hidden or minimized main window keeps optional work paused");

            var beforeResume = gpu.Reads;
            monitor.SetWindowVisible(true);
            await UntilAsync(() => gpu.Reads > beforeResume);
            check(!monitor.OptionalWorkPaused, "game exit and visible window restore normal monitoring");

            Volatile.Write(ref failDetection, 1);
            await UntilAsync(() => monitor.OptionalWorkPaused);
            await AssertQuietAsync(gpu, check, "uncertain process detection suspends optional work conservatively");
            Volatile.Write(ref failDetection, 0);
            var beforeRecovery = gpu.Reads;
            await UntilAsync(() => gpu.Reads > beforeRecovery);
            check(!monitor.OptionalWorkPaused, "successful detection recovers optional monitoring after a query failure");

            for (var i = 0; i < 50; i++) monitor.SetWindowVisible(i % 2 == 0);
            await Task.WhenAll(Enumerable.Range(0, 25).Select(_ => gpu.StartAsync(delay: 0)));
            check(gpu.Starts == 1 && gpu.PeakWorkers == 1, "rapid gaming/visibility policy transitions and repeated starts never duplicate the worker");
            await gpu.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
            check(gpu.Workers == 0, "shutdown cancels a paused GPU worker and leaves no residual task");

            monitor.SetWindowVisible(true);
            var beforeRestart = gpu.Reads;
            await gpu.StartAsync(delay: 0);
            await UntilAsync(() => gpu.Reads > beforeRestart);
            await gpu.StopAsync();
            check(gpu.Starts == 2 && gpu.PeakWorkers == 1 && gpu.Workers == 0, "stopped monitoring restarts safely with no overlap");
        }
        finally
        {
            optional?.Dispose();
            required?.Dispose();
            await gpu.StopAsync();
            await monitor.StopAsync();
        }
        var stoppedDetections = Volatile.Read(ref detections);
        await Task.Delay(60);
        check(detections == stoppedDetections, "presence detector stops completely with its owner");
        check(!HardwareRuntime.ControlsEnabled && Folders.IsReadOnlySession, "gaming policy leaves existing hardware and configuration-write locks intact");
        await CheckHiddenGameNotificationsAsync(check);
    }

    private static async Task CheckHiddenGameNotificationsAsync(Action<bool, string> check)
    {
        var present = 0;
        var detections = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = new GamingActivityMonitor(() =>
        {
            Interlocked.Increment(ref detections);
            return Volatile.Read(ref present) != 0;
        }, TimeSpan.FromMilliseconds(15));
        monitor.Changed += (_, _) =>
        {
            if (monitor.GameRunning) started.TrySetResult();
            else if (started.Task.IsCompleted) exited.TrySetResult();
        };
        try
        {
            // Keep the main window hidden throughout, as with an open tray popup.
            await monitor.StartAsync();
            await UntilAsync(() => Volatile.Read(ref detections) > 0);
            Volatile.Write(ref present, 1);
            await Task.WhenAny(started.Task, Task.Delay(1000));
            check(started.Task.IsCompletedSuccessfully && monitor.OptionalWorkPaused,
                "game start notifies tray consumers while the main window is already hidden");
            Volatile.Write(ref present, 0);
            await Task.WhenAny(exited.Task, Task.Delay(1000));
            check(exited.Task.IsCompletedSuccessfully && monitor.OptionalWorkPaused,
                "hidden-window game exit notifies consumers without resuming optional UI work");
        }
        finally { await monitor.StopAsync(); }
    }

    private static async Task AssertQuietAsync(OptionalGpuProbe gpu, Action<bool, string> check, string label)
    {
        await Task.Delay(40); // Allow the single managed in-flight iteration to retire.
        var before = gpu.Reads;
        await Task.Delay(50);
        check(gpu.Reads == before, label);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException("Gaming transition did not settle.");
            await Task.Delay(5);
        }
    }

    private sealed class OptionalGpuProbe(GamingActivityMonitor monitor) : GPUController(monitor)
    {
        public int Starts;
        public int Workers;
        public int PeakWorkers;
        public int Reads;
        protected override async Task RefreshLoopAsync(int delay, CancellationToken token)
        {
            Interlocked.Increment(ref Starts);
            var workers = Interlocked.Increment(ref Workers);
            PeakWorkers = Math.Max(PeakWorkers, workers);
            try
            {
                while (true)
                {
                    await WaitForBackgroundRefreshAsync(token);
                    token.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref Reads);
                    await Task.Delay(5, token);
                }
            }
            finally { Interlocked.Decrement(ref Workers); }
        }
    }
}
