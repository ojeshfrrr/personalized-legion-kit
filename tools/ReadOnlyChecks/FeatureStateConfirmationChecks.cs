using System;
using System.IO;
using System.Threading.Tasks;
using LenovoLegionToolkit.WPF.Utils;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Utils;

internal static class FeatureStateConfirmationChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var writes = 0;
        check(!HardwareRuntime.ControlsEnabled, "confirmation tests keep the hardware-session lock active");
        try
        {
            await HardwareCommandRunner.RunAsync(() => { writes++; return Task.CompletedTask; });
            check(false, "locked command must fail");
        }
        catch (InvalidOperationException)
        {
            check(writes == 0, "the actual central command runner rejects locked changes before invoking the write delegate");
        }
        var state = new FeatureStateConfirmation<BatteryState>();
        check(state.LastConfirmed is null && !state.IsPending, "feature state starts unknown, never at an invented default");
        state.Observe(BatteryState.Normal);
        state.Begin(BatteryState.Conservation);
        check(state.LastConfirmed == BatteryState.Normal && state.Requested == BatteryState.Conservation && state.IsPending,
            "requested conservation does not replace the confirmed battery mode");
        try { state.Begin(BatteryState.RapidCharge); check(false, "duplicate request must be rejected"); }
        catch (InvalidOperationException) { check(state.Requested == BatteryState.Conservation, "pending mode request cannot be replaced by a competing selection"); }
        state.Observe(BatteryState.Normal);
        check(state.IsPending && state.LastConfirmed == BatteryState.Normal, "a concurrent getter cannot falsely complete a pending command");
        check(state.Complete(BatteryState.Conservation) && !state.IsPending && state.Error is null,
            "matching fresh device readback confirms conservation");
        state.Begin(BatteryState.RapidCharge);
        check(!state.Complete(BatteryState.Normal) && state.LastConfirmed == BatteryState.Normal && state.Error is not null,
            "mismatched readback retains actual hardware mode and reports failure");
        state.Begin(BatteryState.Conservation);
        state.Fail("readback unavailable");
        check(!state.IsPending && state.LastConfirmed == BatteryState.Normal && state.Error == "readback unavailable",
            "unavailable readback preserves only last-confirmed state, never the requested mode");
        state.Observe(BatteryState.RapidCharge);
        check(state.LastConfirmed == BatteryState.RapidCharge && state.Error is not null,
            "failure reconciliation updates observed mode without hiding the failed request");
        state.Begin(BatteryState.Normal);
        check(state.Error is null && state.IsPending, "a new explicit request clears the previous failure");
        state.Complete(BatteryState.Normal);
        try { state.Complete(BatteryState.RapidCharge); check(false, "unsolicited completion must fail"); }
        catch (InvalidOperationException) { check(state.LastConfirmed == BatteryState.Normal, "unsolicited completion cannot relabel the confirmed state"); }
        var power = new FeatureStateConfirmation<PowerModeState>();
        power.Observe(PowerModeState.Balance); power.Begin(PowerModeState.Performance);
        power.Fail("AC unavailable");
        check(power.LastConfirmed == PowerModeState.Balance && !power.IsPending,
            "failed performance-mode request keeps the previous confirmed mode");

        var source = File.ReadAllText("LenovoLegionToolkit.WPF/Controls/AbstractComboBoxFeatureCardControl.cs");
        var change = source[source.IndexOf("protected virtual async Task OnStateChangeAsync", StringComparison.Ordinal)..];
        check(change.IndexOf("HardwareRuntime.RequireControls()", StringComparison.Ordinal) < change.IndexOf("feature.SetStateAsync", StringComparison.Ordinal) &&
            change.Contains("HardwareCommandRunner.RunAsync", StringComparison.Ordinal),
            "feature changes preserve both session permission and the serialized central command gate");
        check(change.IndexOf("feature.IsSupportedAsync()", StringComparison.Ordinal) < change.IndexOf("feature.SetStateAsync", StringComparison.Ordinal) &&
            change.Contains("Array.IndexOf(await feature.GetAllStatesAsync(), selectedState) < 0", StringComparison.Ordinal),
            "unsupported hardware and unsupported requested states are rejected before transmission");
        check(change.Contains("IsRefreshing || Confirmation.IsPending || oldValue is null", StringComparison.Ordinal),
            "population and pending/reconciled selectors cannot start another write");
        var refresh = source[source.IndexOf("protected override async Task OnRefreshAsync", StringComparison.Ordinal)..source.IndexOf("protected override void OnFinishedLoading", StringComparison.Ordinal)];
        check(!refresh.Contains("SetStateAsync", StringComparison.Ordinal) && refresh.Contains("Confirmation.Observe(selectedItem)", StringComparison.Ordinal),
            "page reads populate confirmation from a getter without invoking setters");
        var battery = File.ReadAllText("LenovoLegionToolkit.WPF/Controls/Dashboard/BatteryModeControl.cs");
        check(battery.Contains("Numeric charge thresholds are not reported by this API.", StringComparison.Ordinal) &&
            !battery.Contains("SetStateAsync", StringComparison.Ordinal),
            "battery reports API threshold limitations without inventing percentages or write paths");
        var modes = File.ReadAllText("LenovoLegionToolkit.WPF/Controls/Dashboard/PowerModeControl.cs");
        check(modes.Contains("Confirmation.LastConfirmed is not { } state", StringComparison.Ordinal) &&
            modes.Contains("bool shouldShowButton = confirmedState switch", StringComparison.Ordinal),
            "performance heading and settings availability follow readback rather than requested selection");
    }
}
