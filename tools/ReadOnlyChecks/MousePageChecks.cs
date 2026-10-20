using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Services.Mouse;
using LenovoLegionToolkit.WPF.Presentation;

internal static class MousePageChecks
{
    // Production presentation state + a fake service. No WPF App, IoC or native mouse access.
    public static async Task RunAsync(Action<bool, string> check)
    {
        var fake = new FakeService();
        var page = new MousePageSession(fake, () => true);
        check(fake.TotalCalls == 0 && page.Dpi.Current is null && page.Polling.Current is null,
            "Mouse page construction performs no device I/O and invents no initial values");
        var pendingLocked = false;
        page.Changed += () => { if (page.IsBusy) pendingLocked = !page.CanEditDpi && !page.CanEditPolling; };
        var load = page.ActivateAsync();
        var duplicate = page.ActivateAsync();
        await Task.WhenAll(load, duplicate);
        check(ReferenceEquals(load, duplicate) && fake.DpiReads == 1 && fake.PollingReads == 1 && fake.Writes == 0,
            "Mouse navigation and duplicate loading perform one getter batch and no setters");
        check(page.Dpi.Current?.Value == new MouseDpi(800, 900) && page.Polling.Current?.Value == MousePollingRate.Hz500,
            "Mouse page current values come from fake device responses rather than hardcoded live defaults");
        check(pendingLocked && page.CanEditDpi && page.CanEditPolling && page.Dpi.LastConfirmed is not null,
            "Mouse pending state locks Apply and successful reads expose timestamped last-confirmed state");
        await RejectAsync<ArgumentOutOfRangeException>(() => page.ApplyDpiAsync(1601, 1600));
        await RejectAsync<ArgumentOutOfRangeException>(() => page.ApplyPollingAsync((MousePollingRate)7000));
        check(fake.Writes == 0, "Mouse presentation rejects unsupported DPI and polling values before entering the service");
        await page.ApplyDpiAsync(1650, 1700);
        check(page.Dpi.Current?.Value == new MouseDpi(1650, 1700) && fake.DpiWrites == 1 && fake.PollingWrites == 0 &&
              page.Dpi.Message.StartsWith("Confirmed", StringComparison.Ordinal),
            "explicit DPI Apply shows the backend's verified X/Y result and leaves polling untouched");
        await page.ApplyPollingAsync(MousePollingRate.Hz8000);
        check(page.Polling.Current?.Value == MousePollingRate.Hz8000 && fake.PollingWrites == 1 &&
              page.Polling.Message.StartsWith("Confirmed", StringComparison.Ordinal),
            "explicit polling Apply displays the verified backend readback");

        var last = page.Polling.LastConfirmed;
        fake.PollingResult = new(MousePollingApplyStatus.Uncertain, MousePollingRate.Hz4000,
            MousePollingRate.Hz8000, null, true, "Synthetic timeout; recovery unavailable.");
        await page.ApplyPollingAsync(MousePollingRate.Hz4000);
        check(page.Polling.Current is null && ReferenceEquals(last, page.Polling.LastConfirmed) &&
              !page.CanEditPolling && page.Polling.Message.StartsWith("Not confirmed", StringComparison.Ordinal),
            "unknown polling outcome clears current state, retains historical confirmation and disables Apply");
        await page.RefreshAsync();
        fake.PollingResult = new(MousePollingApplyStatus.Uncertain, MousePollingRate.Hz4000,
            MousePollingRate.Hz8000, MousePollingRate.Hz4000, true, "Separate recovery read confirmed the current rate.");
        await page.ApplyPollingAsync(MousePollingRate.Hz4000);
        check(page.Polling.Current?.Value == MousePollingRate.Hz4000 && page.CanEditPolling &&
              page.Polling.Message.StartsWith("Not confirmed", StringComparison.Ordinal),
            "safe recovery read can show known current polling without claiming the failed write succeeded");
        var lastDpi = page.Dpi.LastConfirmed;
        fake.ApplyError = new IOException("Synthetic service failure");
        await page.ApplyDpiAsync(1700, 1700);
        check(page.Dpi.Current is null && ReferenceEquals(lastDpi, page.Dpi.LastConfirmed) &&
              !page.CanEditDpi && page.Dpi.Message.StartsWith("Failed", StringComparison.Ordinal),
            "service failure never leaves stale DPI labelled current");
        page.Deactivate();
        check(!page.CanEditDpi && !page.CanEditPolling && page.Polling.Current is null && page.Polling.LastConfirmed is not null,
            "navigation retires current readings while retaining explicitly historical last-confirmed state");

        fake = new FakeService();
        page = new MousePageSession(fake, () => false);
        await page.ActivateAsync();
        await RejectAsync<InvalidOperationException>(() => page.ApplyDpiAsync(1650, 1650));
        await RejectAsync<InvalidOperationException>(() => page.ApplyPollingAsync(MousePollingRate.Hz8000));
        check(fake.Writes == 0 && page.Dpi.Current is not null && !page.CanEditDpi && !page.CanEditPolling,
            "read-only Mouse page keeps genuine readings but rejects both Apply paths");
        page.Deactivate();
        fake = new FakeService { PollingError = new NotSupportedException("Synthetic unavailable polling") };
        page = new MousePageSession(fake, () => true);
        await page.ActivateAsync();
        check(page.Dpi.Current is not null && page.Polling.Current is null && page.CanEditDpi && !page.CanEditPolling,
            "unavailable polling does not conceal a valid DPI reading or invent a rate");
        page.Deactivate();
        fake = new FakeService { DpiError = new Win32Exception(5) };
        page = new MousePageSession(fake, () => true);
        await page.ActivateAsync();
        check(fake.DpiReads == 1 && fake.PollingReads == 0 && fake.Writes == 0,
            "access denial stops Mouse refresh without another open/query attempt");
        page.Deactivate();

        fake = new FakeService { BlockDpi = true };
        page = new MousePageSession(fake, () => true);
        var old = page.ActivateAsync();
        await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        page.Deactivate();
        fake.BlockDpi = false;
        var fresh = page.ActivateAsync();
        await Task.WhenAll(old, fresh).WaitAsync(TimeSpan.FromSeconds(2));
        check(fake.Peak == 1 && fake.Active == 0 && fake.DpiReads == 2 && fake.PollingReads == 1 && fake.Writes == 0,
            "rapid Mouse unload/reload drains cancelled work before starting the next getter batch");
        fake.BlockApply = true;
        var apply = page.ApplyPollingAsync(MousePollingRate.Hz8000);
        var calls = fake.TotalCalls;
        page.Deactivate();
        var eventsAfterUnload = 0;
        page.Changed += () => eventsAfterUnload++;
        await apply.WaitAsync(TimeSpan.FromSeconds(2));
        check(fake.Active == 0 && fake.TotalCalls == calls && eventsAfterUnload == 0 && page.Polling.Current is null,
            "Mouse navigation cancels an active Apply with no late UI updates, retry or recovery polling on an inactive page");

        var source = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "LenovoLegionToolkit.WPF", "Pages", "MousePage.xaml.cs"));
        var xaml = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "LenovoLegionToolkit.WPF", "Pages", "MousePage.xaml"));
        check(source.Contains("await _presetApplyTask;", StringComparison.Ordinal) &&
              source.Contains("await _session.ActivateAsync();", StringComparison.Ordinal) &&
              source.Contains("await LoadPresetsAsync();", StringComparison.Ordinal) &&
              source.Contains("Draft_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => RefreshControlAccess();", StringComparison.Ordinal) &&
              source.Contains("PollingSelection_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => RefreshControlAccess();", StringComparison.Ordinal) &&
              xaml.Contains("Click=\"ApplyPolling_Click\"", StringComparison.Ordinal) && xaml.Contains("Click=\"Apply_Click\"", StringComparison.Ordinal),
            "Mouse XAML routes writes only through explicit Apply; draft and selector changes only refresh control access");
        check(xaml.Contains("_lastDpiText", StringComparison.Ordinal) && xaml.Contains("_lastPollingText", StringComparison.Ordinal) &&
              !xaml.Contains("Polling writes await", StringComparison.Ordinal),
            "Mouse UI exposes last-confirmed readings and no longer claims verified polling writes are untested");
    }

    private static async Task RejectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class FakeService : IMouseControlService
    {
        public int DpiReads, PollingReads, DpiWrites, PollingWrites, Active, Peak;
        public int Writes => DpiWrites + PollingWrites;
        public int TotalCalls => DpiReads + PollingReads + Writes;
        public bool BlockDpi, BlockApply;
        public MouseDpi Dpi = new(800, 900);
        public MousePollingRate Rate = MousePollingRate.Hz500;
        public Exception? DpiError, PollingError, ApplyError;
        public MousePollingApplyResult? PollingResult;
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private async Task<T> Run<T>(bool block, Func<T> result, CancellationToken token)
        {
            var active = Interlocked.Increment(ref Active); Peak = Math.Max(Peak, active); Started.TrySetResult();
            try
            {
                await Task.Delay(block ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(1), token);
                return result();
            }
            finally { Interlocked.Decrement(ref Active); }
        }
        public Task<MouseDpi> ReadActiveDpiAsync(CancellationToken token = default)
        {
            DpiReads++;
            return Run(BlockDpi, () => DpiError is not null ? throw DpiError : Dpi, token);
        }
        public Task<MousePollingRate> ReadPollingRateAsync(CancellationToken token = default)
        {
            PollingReads++;
            return Run(false, () => PollingError is not null ? throw PollingError : Rate, token);
        }
        public Task<MouseDpiApplyResult> ApplyActiveDpiAsync(int x, int y, CancellationToken token = default)
        {
            DpiWrites++;
            return Run(BlockApply, () =>
            {
                if (ApplyError is not null) throw ApplyError;
                var before = Dpi; Dpi = DeathAdderV3Protocol.ValidateDpi(x, y);
                return new MouseDpiApplyResult(MouseDpiApplyStatus.Confirmed, Dpi, before, Dpi, true, "Synthetic validated DPI readback.");
            }, token);
        }
        public Task<MousePollingApplyResult> ApplyPollingRateAsync(MousePollingRate rate, CancellationToken token = default)
        {
            PollingWrites++;
            return Run(BlockApply, () =>
            {
                if (ApplyError is not null) throw ApplyError;
                if (PollingResult is not null) return PollingResult;
                var before = Rate; Rate = rate;
                return new MousePollingApplyResult(MousePollingApplyStatus.Confirmed, rate, before, Rate, true, "Synthetic validated polling readback.");
            }, token);
        }
    }
}
