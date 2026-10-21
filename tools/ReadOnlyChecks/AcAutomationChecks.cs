using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Automation;
using LenovoLegionToolkit.Lib.Automation.Pipeline;
using LenovoLegionToolkit.Lib.Automation.Pipeline.Triggers;
using LenovoLegionToolkit.Lib.Automation.Steps;
using LenovoLegionToolkit.Lib.Features;
using LenovoLegionToolkit.Lib.Messaging;
using LenovoLegionToolkit.Lib.Messaging.Messages;
using LenovoLegionToolkit.Lib.Utils;
using LenovoLegionToolkit.WPF.Utils;

internal static class AcAutomationChecks
{
    private sealed class FakeFeature : IFeature<PowerModeState>
    {
        public PowerModeState Actual = PowerModeState.Balance;
        public bool Supported = true, IgnoreWrite, ThrowWrite, ThrowRead;
        public int Reads, Writes;
        public TaskCompletionSource? Started, Release;
        public Task<bool> IsSupportedAsync() => Task.FromResult(Supported);
        public Task<PowerModeState[]> GetAllStatesAsync() => Task.FromResult(new[] { PowerModeState.Quiet, PowerModeState.Balance, PowerModeState.Performance });
        public Task<PowerModeState> GetStateAsync() { Reads++; if (ThrowRead) throw new InvalidOperationException("fake unavailable"); return Task.FromResult(Actual); }
        public async Task SetStateAsync(PowerModeState state)
        {
            Writes++; Started?.TrySetResult();
            if (Release is not null) await Release.Task;
            if (ThrowWrite) throw new InvalidOperationException("fake write failure");
            if (!IgnoreWrite) Actual = state;
        }
    }
    private sealed class FakeStep(PowerModeState state, IFeature<PowerModeState> feature) : AbstractFeatureAutomationStep<PowerModeState>(state, feature)
    {
        public override IAutomationStep DeepCopy() => this;
    }
    private sealed class CountStep : IAutomationStep
    {
        public int Runs;
        public Task<bool> IsSupportedAsync() => Task.FromResult(true);
        public Task RunAsync(AutomationContext context, AutomationEnvironment environment, CancellationToken token) { Runs++; return Task.CompletedTask; }
        public IAutomationStep DeepCopy() => this;
    }
    private static Task Run(FakeFeature feature, PowerModeState target = PowerModeState.Performance, CancellationToken token = default) =>
        new FakeStep(target, feature).RunAsync(new(), new(), token);
    private static async Task Pipeline(AutomationPipeline pipeline, CancellationToken token)
    {
        var method = typeof(AutomationPipeline).GetMethod("RunAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(pipeline, new object[] { new System.Collections.Generic.List<AutomationPipeline>(), token })!;
    }
    private static async Task Reject(Func<Task> action, Action<bool, string> check, string message)
    {
        try { await action(); check(false, message); }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or OperationCanceledException or AggregateException) { check(true, message); }
    }
    public static async Task RunAsync(Action<bool, string> check)
    {
        var transitions = new PowerSourceTransitions();
        transitions.Prime(PowerAdapterStatus.Disconnected);
        check(!transitions.Accept(PowerAdapterStatus.Disconnected), "enabling on battery establishes a baseline without a startup action");
        var accepted = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => Task.Run(() => transitions.Accept(PowerAdapterStatus.Connected))));
        check(accepted.Count(x => x) == 1, "duplicate concurrent AC events are accepted once");
        check(!transitions.Accept(null) && !transitions.Accept(PowerAdapterStatus.Connected), "unknown source neither applies a profile nor resets duplicate suppression");
        check(transitions.Accept(PowerAdapterStatus.Disconnected), "a genuine battery transition is accepted");
        transitions.Prime(PowerAdapterStatus.Connected);
        check(!transitions.Accept(PowerAdapterStatus.Connected), "saving/re-enabling a rule primes state without applying it");
        transitions.Prime(PowerAdapterStatus.Disconnected, generation: 2);
        check(!transitions.Accept(PowerAdapterStatus.Connected, generation: 1), "queued events from an older saved configuration cannot change the baseline");
        check(transitions.Accept(PowerAdapterStatus.Connected, generation: 2), "the new configuration accepts its first genuine transition after rejecting stale work");
        var ac = new ACAdapterConnectedAutomationPipelineTrigger(); var battery = new ACAdapterDisconnectedAutomationPipelineTrigger();
        check(await ac.IsMatchingEvent(new PowerStateAutomationEvent(PowerStateEvent.StatusChange, true, PowerAdapterStatus.Connected, true)) &&
            !await battery.IsMatchingEvent(new PowerStateAutomationEvent(PowerStateEvent.StatusChange, true, PowerAdapterStatus.Connected, true)), "captured AC transition selects the existing AC trigger only");
        check(await battery.IsMatchingEvent(new PowerStateAutomationEvent(PowerStateEvent.StatusChange, true, PowerAdapterStatus.Disconnected, true)), "captured battery transition selects the existing battery trigger");
        check(!await ac.IsMatchingEvent(new PowerStateAutomationEvent(PowerStateEvent.StatusChange, true, null, true)) &&
            !await battery.IsMatchingEvent(new PowerStateAutomationEvent(PowerStateEvent.StatusChange, true, null, true)), "unavailable captured source invokes no inferred AC/battery action");
        check(!await ac.IsMatchingEvent(new PowerStateAutomationEvent(PowerStateEvent.StatusChange, false, PowerAdapterStatus.Connected, true)), "unchanged native power notification never matches the AC transition rule");
        check(!await ac.IsMatchingEvent(new PowerStateAutomationEvent(PowerStateEvent.StatusChange, true, PowerAdapterStatus.ConnectedLowWattage, true)), "low-wattage AC does not imply the normal-AC profile");

        // Scoped test-only opt-in. Folders remains read-only; every device operation below is FakeFeature.
        var flags = AppFlags.Instance; var readOnly = typeof(AppFlags).GetProperty(nameof(AppFlags.ReadOnly))!;
        var preview = typeof(AppFlags).GetProperty(nameof(AppFlags.UiPreview))!;
        var savedReadOnly = flags.ReadOnly; var savedPreview = flags.UiPreview;
        var enabled = typeof(HardwareRuntime).GetField("_controlsEnabled", BindingFlags.Static | BindingFlags.NonPublic)!;
        var savedEnabled = enabled.GetValue(null); var owner = new object(); var publications = 0;
        MessagingCenter.Subscribe<FeatureStateMessage<PowerModeState>>(owner, _ => publications++);
        try
        {
            enabled.SetValue(null, 1); readOnly.SetValue(flags, true); preview.SetValue(flags, false);
            var locked = new FakeFeature(); await Reject(() => Run(locked), check, "read-only blocks automation even with a pre-existing enabled-controls bit");
            check(locked.Reads == 0 && locked.Writes == 0, "read-only rejection occurs before fake provider access");
            readOnly.SetValue(flags, false);
            var success = new FakeFeature(); await Run(success);
            check(success.Writes == 1 && success.Reads == 2 && publications == 1, "successful profile change publishes only after fresh device readback");
            await Run(success); check(success.Writes == 1, "already-confirmed target does not repeat a setter");
            var countBefore = publications; var mismatch = new FakeFeature { IgnoreWrite = true };
            await Reject(() => Run(mismatch), check, "failed readback rejects profile success");
            check(mismatch.Writes == 1 && publications == countBefore, "mismatch sends no retries and publishes no requested-state success");
            var unsupported = new FakeFeature { Supported = false };
            await Reject(() => Run(unsupported), check, "unsupported hardware is rejected");
            check(unsupported.Writes == 0, "unsupported hardware sends no writes");
            var invalid = new FakeFeature(); await Reject(() => Run(invalid, PowerModeState.GodMode), check, "unexposed performance profile is rejected");
            check(invalid.Writes == 0, "unsupported profile is rejected before setter invocation");
            var failed = new FakeFeature { ThrowWrite = true }; await Reject(() => Run(failed), check, "write failure is reported");
            check(failed.Writes == 1, "write failure is not automatically retried");
            var unavailable = new FakeFeature { ThrowRead = true }; await Reject(() => Run(unavailable), check, "unavailable current mode blocks a speculative setter");
            check(unavailable.Writes == 0, "failed initial read sends no write");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); var notStarted = new FakeFeature();
            await Reject(() => Run(notStarted, token: cancelled.Token), check, "cancelled event never starts a command");
            check(notStarted.Reads == 0 && notStarted.Writes == 0, "pre-cancellation leaves the fake provider untouched");

            using var shutdown = new CancellationTokenSource();
            var blocked = new FakeFeature { Started = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            var next = new CountStep(); var pipeline = new AutomationPipeline { Steps = { new FakeStep(PowerModeState.Performance, blocked), next } };
            var pending = Pipeline(pipeline, shutdown.Token); await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var competingWrites = 0;
            await Reject(() => HardwareCommandRunner.RunAsync(() => { competingWrites++; return Task.CompletedTask; }), check, "UI and automation share the same single command gate");
            check(competingWrites == 0, "competing manual command does not execute during automation");
            var beforeCancel = publications; shutdown.Cancel();
            check(!pending.IsCompleted, "shutdown cancellation drains an in-flight write instead of abandoning it");
            blocked.Release.TrySetResult(); await Reject(() => pending, check, "pipeline reports cancellation after the in-flight write drains");
            check(blocked.Reads == 2 && next.Runs == 0 && publications == beforeCancel, "cancelled profile is read back, no later steps run and no success is published");
            var bad = new FakeFeature { IgnoreWrite = true }; var later = new CountStep();
            await Reject(() => Pipeline(new AutomationPipeline { Steps = { new FakeStep(PowerModeState.Performance, bad), later } }, default), check, "unconfirmed pipeline step aborts remaining steps");
            check(later.Runs == 0, "failure cannot silently continue applying more settings");
        }
        finally
        {
            enabled.SetValue(null, savedEnabled); readOnly.SetValue(flags, savedReadOnly); preview.SetValue(flags, savedPreview);
            MessagingCenter.Unsubscribe(owner);
        }
        var processor = File.ReadAllText("LenovoLegionToolkit.Lib.Automation/AutomationProcessor.cs");
        check(processor.Contains("generation != Volatile.Read(ref _generation)", StringComparison.Ordinal) &&
            processor.Contains("using (await _runLock.LockAsync().ConfigureAwait(false)) { }", StringComparison.Ordinal), "processor retires old generations and drains queued work on disable/shutdown");
        var reload = processor[processor.IndexOf("public async Task ReloadPipelinesAsync", StringComparison.Ordinal)..];
        check(reload.IndexOf("HardwareRuntime.RequireControls()", StringComparison.Ordinal) < reload.IndexOf("settings.Store.Pipelines =", StringComparison.Ordinal), "saving rules checks centralized permission before configuration mutation");
    }
}
