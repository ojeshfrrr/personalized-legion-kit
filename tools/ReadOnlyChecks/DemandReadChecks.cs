using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Utils;

internal static class DemandReadChecks
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static async Task RunAsync(Action<bool, string> check)
    {
        var reads = 0; var published = new List<int>(); var errors = 0;
        var session = new DemandReadSession<int>(_ => Task.FromResult(Interlocked.Increment(ref reads)),
            (value, time) => published.Add(value), _ => errors++);
        check(reads == 0, "demand-read construction performs zero reads");
        await session.RefreshAsync();
        check(reads == 0, "inactive explicit refresh cannot query hardware");
        await session.ActivateAsync();
        check(reads == 1 && published.SequenceEqual([1]), "page open reads and publishes exactly once");
        await Task.Delay(80);
        await session.ActivateAsync();
        check(reads == 1, "visible idle and duplicate activation do not schedule another read");
        await session.RefreshAsync();
        check(reads == 2 && published.SequenceEqual([1, 2]), "explicit Refresh obtains a fresh snapshot");
        await session.DeactivateAsync();
        await session.RefreshAsync(); await Task.Delay(80);
        check(reads == 2 && !session.IsActive && !session.IsBusy, "hidden/minimized owner has no query or remaining task");
        await session.ActivateAsync(); await session.DeactivateAsync();
        check(reads == 3 && errors == 0, "returning to a page adds one read without recurring work");

        var started = Signal(); var release = Signal();
        var active = 0; var peak = 0; reads = 0; published.Clear();
        session = new DemandReadSession<int>(async token =>
        {
            var value = Interlocked.Increment(ref reads);
            peak = Math.Max(peak, Interlocked.Increment(ref active));
            try { if (value == 1) { started.TrySetResult(); await release.Task; } return value; }
            finally { Interlocked.Decrement(ref active); }
        }, (value, _) => published.Add(value), _ => errors++);
        var original = session.ActivateAsync(); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var duplicates = Enumerable.Range(0, 50).Select(_ => session.RefreshAsync()).ToArray();
        check(duplicates.All(t => ReferenceEquals(t, original)) && reads == 1, "50 simultaneous refresh requests share the in-flight read");
        var retirement = session.DeactivateAsync();
        check(!retirement.IsCompleted, "non-cancellable native work is drained rather than abandoned");
        var canceledReplacement = session.ActivateAsync();
        _ = session.DeactivateAsync();
        var replacement = session.ActivateAsync();
        await Task.Delay(50);
        check(reads == 1 && published.Count == 0, "rapid hide/show does not overlap a retiring native call or publish hidden results");
        release.TrySetResult();
        await Task.WhenAll(original, canceledReplacement, replacement).WaitAsync(TimeSpan.FromSeconds(5));
        check(reads == 2 && peak == 1 && active == 0, "cancelled queued activation is skipped; replacement runs once after drain");
        check(published.SequenceEqual([2]), "stale native result never reaches the UI");
        await session.DeactivateAsync();

        started = Signal(); release = Signal(); errors = 0;
        session = new DemandReadSession<int>(async token =>
        {
            started.TrySetResult(); await release.Task; throw new IOException("synthetic retired failure");
        }, (_, _) => throw new InvalidOperationException("unexpected publish"), _ => errors++);
        var failed = session.ActivateAsync(); await started.Task;
        _ = session.DeactivateAsync(); release.TrySetResult(); await failed;
        check(errors == 0, "late errors from a retired read cannot update the hidden page");
        reads = 0;
        session = new DemandReadSession<int>(_ => Interlocked.Increment(ref reads) == 1
            ? Task.FromException<int>(new IOException("synthetic failure")) : Task.FromResult(42),
            (value, _) => check(value == 42, "explicit retry publishes genuine replacement response"), _ => errors++);
        await session.ActivateAsync();
        check(errors == 1 && reads == 1 && !session.IsBusy, "active failure is reported once without an automatic retry");
        await session.RefreshAsync(); await session.DeactivateAsync();
        check(reads == 2, "only explicit Refresh retries a failed read");

        // Deterministic five-tick replay, not a production CPU/FPS benchmark.
        var oldQueries = 1; var newQueries = 0;
        var replay = new DemandReadSession<int>(_ => Task.FromResult(++newQueries), (_, _) => { }, _ => { });
        await replay.ActivateAsync();
        for (var tick = 0; tick < 5; tick++) { oldQueries++; await Task.Delay(10); }
        check(oldQueries == 6 && newQueries == 1, "five idle timer ticks: former schedule 6 queries; demand session 1 initial query");
        await replay.DeactivateAsync();
        var evidence = Path.Combine(Environment.CurrentDirectory, "BuildProjectLegion", "DemandReads");
        Directory.CreateDirectory(evidence);
        File.WriteAllText(Path.Combine(evidence, "query-counts.json"),
            System.Text.Json.JsonSerializer.Serialize(new { Method = "five synthetic timer ticks; fake getters only", FormerSchedule = oldQueries,
                DemandVisible = newQueries, DemandHiddenAdditional = 0, PeakConcurrentReads = peak, ProductionMemoryMeasured = false },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        VerifyRouting(check);
    }
    private static void VerifyRouting(Action<bool, string> check)
    {
        string Source(string path) => File.ReadAllText(Path.Combine(Environment.CurrentDirectory, path));
        const string dashboard = "LenovoLegionToolkit.WPF/Controls/Dashboard/";
        foreach (var name in new[] { "SensorsControl", "SensorsControlV2", "DiscreteGPUControl" })
        {
            var source = Source(dashboard + name + ".xaml.cs");
            check(source.Contains("DemandReadSession<") && !source.Contains("Task.Delay(") &&
                !source.Contains("AcquireOptionalMonitoring(") && !source.Contains(".Subscribe(TimeSpan"),
                name + ": shared read session; no independent timer or GPU monitoring lease");
            check(source.Contains("_reads.DeactivateAsync()") && source.Contains("_reads.ActivateAsync()"),
                name + ": activation reads and hiding cancels its owner");
        }
        var battery = Source("LenovoLegionToolkit.WPF/Pages/BatteryPage.xaml.cs");
        check(battery.Contains("DemandReadSession<") && !battery.Contains("Task.Delay(") && battery.Contains("_reads.DeactivateAsync()"),
            "Battery: no independent two-second loop; owner deactivation retires its read");
        var gpu = Source("LenovoLegionToolkit.Lib/Controllers/GPUController.cs");
        var policy = gpu[gpu.IndexOf("private void UpdatePollingPolicy()")..gpu.IndexOf("protected Task WaitForBackgroundRefreshAsync")];
        check(!policy.Contains("LockedPStateId") && policy.Contains("_pStateEnforcementEnabled"),
            "saved P-state alone cannot enable background enforcement");
        var getter = gpu[gpu.IndexOf("public async Task<GPUStatus> RefreshNowAsync")..gpu.IndexOf("public async Task StartAsync")];
        check(!getter.Contains("ApplyPState") && getter.Contains("DoRefreshAsync(token)"), "one-shot GPU getter does not opt into P-state writes");
        var sensor = Source("LenovoLegionToolkit.Lib/Controllers/Sensors/AbstractSensorsController.cs");
        check(!sensor.Contains("gpuController.StartAsync"), "sensor snapshot cannot start a GPU polling worker");
        var app = Source("LenovoLegionToolkit.WPF/App.xaml.cs");
        var startup = app[app.IndexOf("private Task StartBackgroundServicesAsync()")..app.IndexOf("private Task InitializeUIAsync()")];
        check(!startup.Contains("InitHWiNFOAsync") && startup.Contains("Task.CompletedTask"), "saved HWiNFO preference cannot start background access at startup");
        var initOsd = app[app.IndexOf("public void InitOsd()")..app.IndexOf("private void InitAppMessages()")];
        check(!initOsd.Contains("HandleOsdCommand(ToggleState.On") && !initOsd.Contains("Store.ShowOsd"),
            "saved OSD preference cannot start hardware reads automatically");
        check(app.Contains("if (show && !HardwareRuntime.ControlsEnabled) return;") && app.Contains("if (!restoreOsd || !HardwareRuntime.ControlsEnabled"),
            "OSD requires explicit session permission; shell recreation only restores an active overlay");
        var integration = Source("LenovoLegionToolkit.Lib/Integrations/HWiNFOIntegration.cs");
        check(integration.Contains("if (_cts is null && _refreshTask is null) return;") && integration.Contains("SemaphoreSlim _lifecycle") && integration.Contains("HardwareRuntime.RequireControls()"),
            "HWiNFO serializes worker ownership, requires session opt-in, and does not clear unowned registry state");
        var macro = Source("LenovoLegionToolkit.WPF/Pages/MacroPage.xaml.cs");
        check(macro.Contains("StateChanged += Controller_StateChanged") && macro.Contains("StateChanged -= Controller_StateChanged") && macro.Contains("if (_subscribed)"),
            "Macro page attaches once, detaches, and rejects inactive queued callbacks");
        var activity = Source("LenovoLegionToolkit.WPF/Utils/OptionalUiActivity.cs");
        check(activity.Contains("OptionalUiPolicy.Evaluate") && activity.Contains("WindowState.Minimized") && activity.Contains("_monitor.OptionalWorkPaused") && activity.Contains("_monitor.Changed -= OnPolicyChanged"),
            "UI activation depends on visibility/minimize policy and detaches its global policy handler");
        var advanced = Source(dashboard + "SensorsControlV2.xaml.cs");
        check(advanced.Contains("IsLibreHardwareMonitorInitialized()") && !advanced.Contains("_sensorsGroupControllers.InitializeAsync("),
            "advanced snapshot never initializes a low-level provider just to populate the page");
        var management = Source("LenovoLegionToolkit.WPF/Windows/Dashboard/DiscreteGPUManagementWindow.xaml.cs");
        check(management.Contains("RetireAppRead()") && management.Contains("await _appReadTask") && management.Contains("token.ThrowIfCancellationRequested()") && !management.Contains(".Refreshed +="),
            "GPU management drains/cancels its owned app-list read and has no recurring event subscription");
        var mouse = Source("LenovoLegionToolkit.WPF/Pages/MousePage.xaml.cs");
        check(mouse.Contains("OptionalUiActivity") && mouse.Contains("_buttons?.Deactivate()") && mouse.Contains("_session.Deactivate()"),
            "Mouse visibility policy retires both query sessions without changing device protocols");
        var main = Source("LenovoLegionToolkit.WPF/Windows/MainWindow.xaml.cs");
        check(main.Contains("ButtonBase { Tag: \"ReadOnlyRefresh\" }") && !main.Contains("Tag: \"Apply\""),
            "read-only input routing admits explicitly tagged getter buttons only");
    }
}
