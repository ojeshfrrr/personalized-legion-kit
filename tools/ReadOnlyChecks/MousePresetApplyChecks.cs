using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Services.Mouse;
using LenovoLegionToolkit.WPF.Presentation;

internal static class MousePresetApplyChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var preset = new MouseSoftwareProfile(Guid.NewGuid(), "Offline preset", new(800, 900), MousePollingRate.Hz500,
            new Dictionary<byte, MouseButtonBinding> { [5] = MouseButtonBinding.Key(new(MouseKeyboardKey.C, MouseKeyboardModifiers.LeftCtrl)),
                [4] = MouseButtonBinding.Basic(MouseButtonAction.Forward) });
        var fake = new Fake();
        var permission = true;
        var applier = new MousePresetApplier(fake, fake, () => permission);
        check(fake.Calls.Count == 0, "preset coordinator construction performs no device work");
        foreach (var invalid in new[] { preset with { Dpi = new(0, 900) }, preset with { PollingRate = (MousePollingRate)123 },
            preset with { Assignments = new Dictionary<byte, MouseButtonBinding> { [1] = MouseButtonBinding.Basic(MouseButtonAction.Back) } },
            preset with { Assignments = new Dictionary<byte, MouseButtonBinding> { [255] = MouseButtonBinding.Basic(MouseButtonAction.Back) } },
            preset with { Assignments = new Dictionary<byte, MouseButtonBinding> { [4] = MouseButtonBinding.Key(new((MouseKeyboardKey)255)) } } })
        {
            var rejected = false;
            try { await applier.ApplyAsync(invalid); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { rejected = true; }
            check(rejected && fake.Calls.Count == 0, "entire invalid preset is rejected before any read or write");
        }
        permission = false;
        await RejectAsync<InvalidOperationException>(() => applier.ApplyAsync(preset));
        check(fake.Calls.Count == 0, "read-only preset guard rejects all device access before preflight");
        permission = true;
        var success = await applier.ApplyAsync(preset);
        check(success.Success && fake.Calls.SequenceEqual(new[] { "ReadDpi", "ReadPolling", "ReadBank", "Dpi", "Polling", "Button4", "Button5" }) &&
            success.Steps.Count == 5 && success.Steps.Skip(1).All(s => s.ReadAt.HasValue),
            "preset applies sequentially through existing APIs after full preflight; each successful setting has verified readback");
        check(success.Steps[3].ButtonReadback?.KnownAction == MouseButtonAction.Forward &&
            success.Steps[4].ButtonReadback?.KnownKeyboard == new MouseKeyboardAssignment(MouseKeyboardKey.C, MouseKeyboardModifiers.LeftCtrl),
            "preset supports typed mouse and left/right-modifier keyboard intent without alternate protocols");

        fake = new Fake { BadBank = true }; applier = new(fake, fake, () => true);
        var badBank = await applier.ApplyAsync(preset);
        check(!badBank.Success && fake.Calls.Count == 3 && badBank.Steps.Skip(1).All(s => s.Status == MousePresetStepStatus.NotAttempted),
            "wrong-bank baseline prevents all setting operations");
        fake = new Fake { UnknownOriginal = true }; applier = new(fake, fake, () => true);
        check(!(await applier.ApplyAsync(preset)).Success && fake.Calls.Count == 3,
            "unsupported original assignment fails entire preflight before any writes");
        fake = new Fake { MissingOriginal = true }; applier = new(fake, fake, () => true);
        check(!(await applier.ApplyAsync(preset)).Success && fake.Calls.Count == 3,
            "missing baseline target fails preflight without setting operations");

        fake = new Fake { FailPolling = true }; applier = new(fake, fake, () => true);
        var partial = await applier.ApplyAsync(preset);
        check(!partial.Success && partial.Steps[1].Status == MousePresetStepStatus.Confirmed &&
            partial.Steps[2].Status == MousePresetStepStatus.Uncertain && partial.Steps[2].PollingReadback is null &&
            partial.Steps.Skip(3).All(s => s.Status == MousePresetStepStatus.NotAttempted) && fake.Calls.Count == 5,
            "partial failure keeps confirmed DPI, marks polling uncertain and never attempts later buttons or rollback");
        check(partial.Summary.Contains("write attempted", StringComparison.Ordinal) && partial.Summary.Contains("NotAttempted", StringComparison.Ordinal),
            "preset result exposes per-setting attempted and unattempted outcomes");
        fake = new Fake { FailPolling = true, MatchingFailedReadback = true }; applier = new(fake, fake, () => true);
        check(!(await applier.ApplyAsync(preset)).Success && fake.Calls.Count == 5,
            "matching readback does not override a failed acknowledgement or continue a preset");
        fake = new Fake { MismatchDpi = true }; applier = new(fake, fake, () => true);
        check(!(await applier.ApplyAsync(preset)).Success && fake.Calls.Count == 4,
            "successful status with wrong DPI readback stops before polling");
        fake = new Fake { ThrowPolling = true }; applier = new(fake, fake, () => true);
        var throwing = await applier.ApplyAsync(preset);
        check(throwing.Steps[2].Status == MousePresetStepStatus.Uncertain && throwing.Steps[2].WriteAttempted is null && fake.Calls.Count == 5,
            "throwing operation reports unknown transmission and does not retry or continue");
        fake = new Fake { FailButton = true }; applier = new(fake, fake, () => true);
        var buttonFailure = await applier.ApplyAsync(preset);
        check(!buttonFailure.Success && buttonFailure.Steps[3].Status == MousePresetStepStatus.Uncertain &&
            buttonFailure.Steps[4].Status == MousePresetStepStatus.NotAttempted && fake.Calls.Count == 6,
            "first button ambiguity stops the later keyboard assignment while preserving earlier verified DPI/polling");
        fake = new Fake { NoChanges = true }; applier = new(fake, fake, () => true);
        var unchanged = await applier.ApplyAsync(preset);
        check(unchanged.Success && unchanged.Steps.Skip(1).All(s => s.Status == MousePresetStepStatus.Unchanged && s.WriteAttempted == false && s.ReadAt.HasValue),
            "backend-verified unchanged settings remain successful readbacks without claiming writes");
        permission = true;
        fake = new Fake { AfterDpi = () => permission = false }; applier = new(fake, fake, () => permission);
        var lost = await applier.ApplyAsync(preset);
        check(!lost.Success && lost.Steps[2].Status == MousePresetStepStatus.Failed && lost.Steps[2].WriteAttempted == false && fake.Calls.Count == 4,
            "permission loss between operations prevents the next write");
        using var cancellation = new CancellationTokenSource();
        fake = new Fake { AfterDpi = cancellation.Cancel }; applier = new(fake, fake, () => true);
        var cancelled = await applier.ApplyAsync(preset, cancellation.Token);
        check(!cancelled.Success && fake.Calls.Count == 4 && cancelled.Steps.Skip(3).All(s => s.Status == MousePresetStepStatus.NotAttempted),
            "page cancellation stops remaining preset operations without recovery polling");
        fake.AfterDpi = null;
        check((await applier.ApplyAsync(preset)).Success, "cancelled preset releases its gate for a later explicit operation");
        fake = new Fake { BlockRead = true }; applier = new(fake, fake, () => true);
        var pending = applier.ApplyAsync(preset);
        await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await RejectAsync<InvalidOperationException>(() => applier.ApplyAsync(preset));
        fake.Release.TrySetResult(true);
        check((await pending).Success && fake.Calls.Count(c => c == "ReadDpi") == 1,
            "duplicate preset Apply is rejected rather than queued or overlapped");
        fake = new Fake(); applier = new(fake, fake, () => true);
        var withoutButtons = await applier.ApplyAsync(preset with { Assignments = new Dictionary<byte, MouseButtonBinding>() });
        check(withoutButtons.Success && !fake.Calls.Contains("ReadBank") && !fake.Calls.Any(c => c.StartsWith("Button", StringComparison.Ordinal)),
            "DPI/poll-only preset performs no assignment enumeration or writes");

        fake = new Fake();
        var session = new MousePageSession(fake, () => true);
        await session.ActivateAsync();
        session.InvalidateForPreset();
        check(session.Dpi.Current is null && session.Dpi.LastConfirmed is not null,
            "preset start invalidates current readings but preserves last-confirmed history");
        session.AcceptPresetResult(partial);
        check(session.Dpi.Current?.Value == preset.Dpi && session.Polling.Current is null && session.Polling.LastConfirmed is not null,
            "partial preset publishes only verified successful readings; uncertain values stay unavailable");
        session.Deactivate();
        session.AcceptPresetResult(success);
        check(session.Dpi.Current is null && session.Polling.Current is null,
            "retired page never accepts delayed preset readings");
        var buttons = new MouseButtonsSession(fake, () => true); buttons.Activate();
        buttons.AcceptPresetResult(success);
        check(buttons.Current?.Assignments.Single(a => a.Button == 4).KnownAction == MouseButtonAction.Forward && buttons.RestorePoint is null,
            "verified preset readbacks update assignments without inventing a batch Restore action");
        buttons.InvalidateForPreset(); buttons.AcceptPresetResult(partial);
        check(buttons.Current is null && buttons.RestorePoint is null,
            "partial batch leaves assignment bank unavailable until a new explicit read");
        buttons.Deactivate();

        var source = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "LenovoLegionToolkit.WPF", "Pages", "MousePage.Presets.cs"));
        var select = source[source.IndexOf("private void PresetSelection_Changed", StringComparison.Ordinal)..source.IndexOf("private static string DescribePreset", StringComparison.Ordinal)];
        check(!select.Contains("ApplyAsync", StringComparison.Ordinal) && !select.Contains("Save(", StringComparison.Ordinal) &&
            select.Contains("RenderPresetSelection", StringComparison.Ordinal), "preset selection and inspection contain no setters or persistence calls");
        var query = source[source.IndexOf("private bool IsPresetQuerySource", StringComparison.Ordinal)..];
        check(query.Contains("CanPersistPresets() &&", StringComparison.Ordinal) && !query.Contains("ReferenceEquals(source, _presetApply)", StringComparison.Ordinal) &&
            source.Contains("!Folders.IsReadOnlySession && !AppFlags.Instance.ReadOnly && !AppFlags.Instance.UiPreview", StringComparison.Ordinal),
            "query routing never exempts hardware Apply and local mutations respect all read-only/preview guards");
        var load = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "LenovoLegionToolkit.WPF", "Pages", "MousePage.xaml.cs"));
        var startup = load[load.IndexOf("private async Task ActivateQueriesAsync", StringComparison.Ordinal)..load.IndexOf("private void Page_Unloaded", StringComparison.Ordinal)];
        check(!startup.Contains("ApplyPreset", StringComparison.Ordinal) && startup.Contains("await _presetApplyTask", StringComparison.Ordinal) && startup.Contains("LoadPresetsAsync", StringComparison.Ordinal),
            "navigation drains a retiring batch and loads local intent without applying it");
    }
    private static async Task RejectAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static MouseButtonAssignment Assignment(byte id, MouseButtonBinding binding)
    {
        var bytes = DeathAdderV3ButtonProtocol.CreateBindingSet(id, binding, 1);
        return new(1, id, 0, bytes[12], bytes[13], Convert.ToHexString(bytes.AsSpan(14, 5)));
    }
    private sealed class Fake : IMouseControlService, IMouseButtonService
    {
        public readonly List<string> Calls = [];
        public bool BadBank, UnknownOriginal, MissingOriginal, FailPolling, MatchingFailedReadback, ThrowPolling, MismatchDpi, BlockRead, FailButton, NoChanges;
        public Action? AfterDpi;
        public readonly TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<MouseDpi> ReadActiveDpiAsync(CancellationToken token = default)
        { Calls.Add("ReadDpi"); Entered.TrySetResult(true); if (BlockRead) await Release.Task.WaitAsync(token); return new(1600, 1600); }
        public Task<MousePollingRate> ReadPollingRateAsync(CancellationToken token = default)
        { Calls.Add("ReadPolling"); return Task.FromResult(MousePollingRate.Hz2000); }
        public Task<MouseProfileBanks> ReadProfileBanksAsync(CancellationToken token = default) => Task.FromResult(new MouseProfileBanks(1, new byte[] { 1 }));
        public Task<MouseButtonBank> ReadButtonBankAsync(byte profile, CancellationToken token = default)
        {
            Calls.Add("ReadBank");
            MouseButtonAssignment original = UnknownOriginal ? new(1, 4, 0, 255, 1, "0400000000") : Assignment(4, MouseButtonBinding.Basic(MouseButtonAction.Back));
            return Task.FromResult(new MouseButtonBank(BadBank ? (byte)2 : (byte)1,
                MissingOriginal ? new[] { Assignment(5, MouseButtonBinding.Basic(MouseButtonAction.Forward)) }
                    : new[] { original, Assignment(5, MouseButtonBinding.Basic(MouseButtonAction.Forward)) }));
        }
        public Task<MouseDpiApplyResult> ApplyActiveDpiAsync(int x, int y, CancellationToken token = default)
        {
            Calls.Add("Dpi"); AfterDpi?.Invoke();
            return Task.FromResult(new MouseDpiApplyResult(NoChanges ? MouseDpiApplyStatus.Unchanged : MouseDpiApplyStatus.Confirmed, DeathAdderV3Protocol.ValidateDpi(x, y), new(1600, 1600), MismatchDpi ? new(1000, 1000) : DeathAdderV3Protocol.ValidateDpi(x, y), !NoChanges, "Fake fresh readback"));
        }
        public Task<MousePollingApplyResult> ApplyPollingRateAsync(MousePollingRate rate, CancellationToken token = default)
        {
            Calls.Add("Polling"); if (ThrowPolling) throw new IOException("Fake uncertain disconnect");
            return Task.FromResult(new MousePollingApplyResult(FailPolling ? MousePollingApplyStatus.Uncertain : NoChanges ? MousePollingApplyStatus.Unchanged : MousePollingApplyStatus.Confirmed,
                rate, MousePollingRate.Hz2000, FailPolling && !MatchingFailedReadback ? null : rate, !NoChanges, "Fake fresh readback"));
        }
        public Task<MouseButtonApplyResult> ApplyButtonAsync(MouseButtonAssignment expected, MouseButtonAction action, CancellationToken token = default) =>
            Bind(expected, MouseButtonBinding.Basic(action));
        public Task<MouseButtonApplyResult> ApplyKeyboardAsync(MouseButtonAssignment expected, MouseKeyboardAssignment assignment, CancellationToken token = default) =>
            Bind(expected, MouseButtonBinding.Key(assignment));
        private Task<MouseButtonApplyResult> Bind(MouseButtonAssignment expected, MouseButtonBinding binding)
        { Calls.Add("Button" + expected.Button); return Task.FromResult(new MouseButtonApplyResult(FailButton ? MouseButtonApplyStatus.Uncertain : NoChanges ? MouseButtonApplyStatus.Unchanged : MouseButtonApplyStatus.Confirmed, expected, Assignment(expected.Button, binding), !NoChanges, "Fake fresh readback")); }
    }
}
