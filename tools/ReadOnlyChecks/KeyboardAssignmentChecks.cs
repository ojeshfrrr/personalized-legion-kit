using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Services.Mouse;
using LenovoLegionToolkit.WPF.Presentation;

internal static class KeyboardAssignmentChecks
{
    // Synthetic transport/presentation checks only. No physical keyboard setting reports.
    public static async Task RunAsync(Action<bool, string> check)
    {
        var requested = new MouseKeyboardAssignment(MouseKeyboardKey.C, MouseKeyboardModifiers.LeftCtrl);
        var wire = DeathAdderV3ButtonProtocol.CreateKeyboardSet(4, requested, 1);
        check(wire[6] == 10 && wire[7] == 2 && wire[8] == 0x0C && wire[9] == 1 && wire[10] == 4 && wire[11] == 0 &&
              wire[12] == 2 && wire[13] == 2 && wire[14] == 1 && wire[15] == 6 && wire[89] == 6 && wire.Skip(16).Take(73).All(b => b == 0),
            "Ctrl+C keyboard mapping matches product-178 function-2 modifier/HID wire vector");
        var cases = 0;
        foreach (var key in MouseKeyboardAssignments.Keys)
        for (var modifiers = 0; modifiers <= 255; modifiers++)
        {
            var value = new MouseKeyboardAssignment(key, (MouseKeyboardModifiers)modifiers);
            var frame = DeathAdderV3ButtonProtocol.CreateKeyboardSet(4, value, 1);
            DeathAdderV3ButtonProtocol.ValidateSetter(frame);
            var ack = (byte[])frame.Clone(); ack[1] = 2;
            DeathAdderV3ButtonProtocol.ValidateBindingAcknowledgement(ack, 4, MouseButtonBinding.Key(value), 1);
            if (DeathAdderV3ButtonProtocol.ParseAssignment(ack, 1, 4, 1, true).KnownKeyboard != value) throw new Exception("Keyboard roundtrip failed.");
            cases++;
        }
        check(cases == 53 * 256, "all 53 allowed keys and 256 modifier masks round-trip offline without inferring multi-key sequence support");
        foreach (var key in new[] { 0, 1, 2, 3, 45, 224, 255, 256, 65535 })
            Reject<ArgumentOutOfRangeException>(() => DeathAdderV3ButtonProtocol.CreateKeyboardSet(4, new((MouseKeyboardKey)key), 1));
        foreach (var modifiers in new[] { 256, 512, 65535 })
            Reject<ArgumentOutOfRangeException>(() => DeathAdderV3ButtonProtocol.CreateKeyboardSet(4, new(MouseKeyboardKey.A, (MouseKeyboardModifiers)modifiers), 1));
        Reject<InvalidOperationException>(() => DeathAdderV3ButtonProtocol.CreateKeyboardSet(1, requested, 1));
        Reject<ArgumentException>(() => new MouseButtonBinding(MouseButtonAction.Back, requested).Validate());
        Reject<ArgumentException>(() => new MouseButtonBinding(null, null).Validate());
        check(true, "modifier-only/unknown keys, unknown modifier bits, primary-click remaps and ambiguous binding types are rejected before transmission");
        foreach (var index in new[] { 0, 6, 7, 8, 9, 11, 13, 16, 19, 89, 90 })
        {
            var bad = (byte[])wire.Clone(); bad[index] ^= 1;
            try { DeathAdderV3ButtonProtocol.ValidateSetter(bad); throw new Exception("Accepted altered keyboard setter."); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { }
        }
        check(MouseKeyboardAssignments.Decode(2, 2, "0106000001") is null && MouseKeyboardAssignments.Decode(2, 1, "0106000000") is null &&
              MouseKeyboardAssignments.Decode(2, 2, "0100000000") is null,
            "noncanonical keyboard data or unsupported function shapes remain unavailable rather than guessed");
        var ackGood = (byte[])wire.Clone(); ackGood[1] = 2;
        var stale = (byte[])ackGood.Clone(); stale[2] = 2;
        Reject<InvalidDataException>(() => DeathAdderV3ButtonProtocol.ValidateBindingAcknowledgement(stale, 4, MouseButtonBinding.Key(requested), 1));
        var mismatch = (byte[])ackGood.Clone(); mismatch[14] = 2; Checksum(mismatch);
        Reject<InvalidDataException>(() => DeathAdderV3ButtonProtocol.ValidateBindingAcknowledgement(mismatch, 4, MouseButtonBinding.Key(requested), 1));
        check(true, "keyboard acknowledgements require matching transaction, checksum and exact requested modifier/key pair");

        var fake = new FakeTransport(); var client = Client(fake); var original = Assignment(MouseButtonBinding.Basic(MouseButtonAction.Back));
        var result = await client.ApplyKeyboardAsync(original, requested);
        check(result.Success && result.Actual?.KnownKeyboard == requested && fake.Writes == 1 && fake.Commands.SequenceEqual(new byte[] { 0x8A, 0x80, 0x81, 0x8C, 0x0C, 0x8C }),
            "keyboard Apply reuses verified bank/original/ack/readback pipeline and only reports confirmed readback as success");
        result = await client.ApplyKeyboardAsync(result.Actual!, requested);
        check(result.Status == MouseButtonApplyStatus.Unchanged && fake.Writes == 1, "already-matching keyboard mapping sends no redundant setter");
        result = await client.ApplyAsync(result.Actual!, MouseButtonAction.Back);
        check(result.Success && result.Actual?.KnownAction == MouseButtonAction.Back && fake.Writes == 2,
            "verified pipeline can restore a canonical keyboard original back to a basic mouse assignment");
        var calls = fake.Commands.Count;
        await RejectAsync<ArgumentOutOfRangeException>(() => client.ApplyKeyboardAsync(original, new((MouseKeyboardKey)0)));
        check(fake.Commands.Count == calls, "invalid keyboard values are rejected before bank discovery or device traffic");
        fake = new() { IgnoreWrite = true }; result = await Client(fake).ApplyKeyboardAsync(original, requested);
        check(!result.Success && result.Status == MouseButtonApplyStatus.ReadbackMismatch && fake.Writes == 1,
            "keyboard setter acknowledgement with a different fresh readback is a failure");
        fake = new() { BadAck = true }; result = await Client(fake).ApplyKeyboardAsync(original, requested);
        check(!result.Success && result.Status == MouseButtonApplyStatus.Uncertain && result.Actual?.KnownKeyboard == requested && fake.Writes == 1,
            "malformed keyboard acknowledgement permits one safe readback without claiming write success or retrying");
        fake = new() { BadAck = true, FailReadback = true }; result = await Client(fake).ApplyKeyboardAsync(original, requested);
        check(result.Actual is null && fake.Commands.Count == 6 && fake.Writes == 1,
            "failed keyboard recovery is explicitly unknown and not retried");
        fake = new() { WriteError = new IOException("Synthetic disconnection") }; result = await Client(fake).ApplyKeyboardAsync(original, requested);
        check(!result.Success && fake.Writes == 1, "keyboard disconnection never becomes send-only success");
        fake = new() { WriteError = new Win32Exception(5) }; result = await Client(fake).ApplyKeyboardAsync(original, requested);
        check(result.Actual is null && fake.Commands.Count == 5, "denied keyboard write stops without repeated access attempts");
        fake = new() { BlockWrite = true }; result = await Client(fake, 40).ApplyKeyboardAsync(original, requested);
        check(!result.Success && result.Actual?.KnownKeyboard == requested && fake.Active == 0 && fake.Writes == 1,
            "keyboard timeout drains work and uses a separate bounded recovery getter");
        using (var cancel = new CancellationTokenSource())
        {
            fake = new() { OnWrite = cancel.Cancel }; result = await Client(fake).ApplyKeyboardAsync(original, requested, cancel.Token);
            check(result.Actual is null && fake.Commands.Count == 5 && fake.Active == 0,
                "keyboard navigation cancellation skips recovery and leaves no worker");
        }
        fake = new() { Delay = TimeSpan.FromMilliseconds(2) }; client = Client(fake);
        var apply = client.ApplyKeyboardAsync(original, requested); var read = client.ReadProfilesAsync();
        await Task.WhenAll(apply, read);
        check(fake.Peak == 1 && fake.Commands.Take(6).Last() == 0x8C, "keyboard writes serialize whole transactions with concurrent profile reads");
        await RejectAsync<InvalidOperationException>(() => new MouseControlService().ApplyKeyboardAsync(original, requested));
        check(true, "real keyboard setter remains blocked in the read-only assertion profile before discovery");
        await Presentation(check, requested);
    }
    private static async Task Presentation(Action<bool, string> check, MouseKeyboardAssignment requested)
    {
        var fake = new FakeTransport(); var page = new MouseButtonsSession(new FakeService(fake), () => true); page.Activate();
        check(fake.Commands.Count == 0, "keyboard UI initialization and navigation never construct a device write");
        await page.ReadProfilesAsync(); await page.ReadAssignmentsAsync();
        await page.ApplyKeyboardAsync(4, requested);
        check(page.Current?.Assignments.Single(a => a.Button == 4).KnownKeyboard == requested && page.CanRestore &&
              page.RestorePoint?.Original.KnownAction == MouseButtonAction.Back,
            "keyboard UI distinguishes the confirmed key readback and retains an exact explicit restore point");
        await page.RestoreAsync();
        check(page.Current?.Assignments.Single(a => a.Button == 4).KnownAction == MouseButtonAction.Back && !page.CanRestore &&
              page.Status.StartsWith("Restoration verified", StringComparison.Ordinal) && fake.Writes == 2,
            "Restore is an explicit operation with verified original readback, never an automatic side effect");
        await page.ApplyKeyboardAsync(4, requested);
        fake.Current = MouseButtonBinding.Basic(MouseButtonAction.Forward);
        var writes = fake.Writes;
        await page.RestoreAsync();
        check(!page.CanRestore && fake.Writes == writes && page.Status.StartsWith("Not confirmed", StringComparison.Ordinal),
            "an externally changed assignment blocks stale restoration without another setter");
        page.Deactivate();
        fake = new(); page = new(new FakeService(fake), () => false); page.Activate(); await page.ReadProfilesAsync(); await page.ReadAssignmentsAsync();
        await RejectAsync<InvalidOperationException>(() => page.ApplyKeyboardAsync(4, requested));
        check(fake.Writes == 0 && !page.CanRestore, "read-only keyboard UI cannot Apply or Restore settings"); page.Deactivate();
        var source = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "LenovoLegionToolkit.WPF", "Pages", "MousePage.Buttons.cs"));
        var xaml = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "LenovoLegionToolkit.WPF", "Pages", "MousePage.xaml"));
        check(xaml.Contains("_buttonConfirmedValue", StringComparison.Ordinal) && xaml.Contains("_buttonRequestedValue", StringComparison.Ordinal) &&
              xaml.Contains("RestoreButtonAssignment_Click", StringComparison.Ordinal) &&
              source.Contains("if (!_renderingButtons) RefreshButtonAccess();", StringComparison.Ordinal) &&
              !source[source.IndexOf("private bool IsButtonQuerySource", StringComparison.Ordinal)..].Contains("_restoreButtonAssignment", StringComparison.Ordinal),
            "UI separates requested/confirmed values; modifier edits are local and read-only routing never enables Restore");
        var localSelection = source[source.IndexOf("private void SelectSchematicButton", StringComparison.Ordinal)..
            source.IndexOf("private async void ApplyButtonAssignment_Click", StringComparison.Ordinal)];
        check(localSelection.Contains("_buttonTargetSelector.SelectedItem", StringComparison.Ordinal) &&
              !localSelection.Contains("ApplyAsync", StringComparison.Ordinal) &&
              !localSelection.Contains("ApplyKeyboardAsync", StringComparison.Ordinal) &&
              !localSelection.Contains("ReadBankAsync", StringComparison.Ordinal),
            "schematic and assignment-row selection only update the local target, with no hardware operations");
        var markup = System.Xml.Linq.XDocument.Parse(xaml);
        var regions = markup.Descendants().Where(e => e.Name.LocalName == "RadioButton")
            .Select(e => byte.Parse(e.Attribute("Tag")!.Value)).OrderBy(id => id).ToArray();
        check(regions.SequenceEqual(DeathAdderV3ButtonProtocol.Buttons.Select(b => b.Id).OrderBy(id => id)) &&
              xaml.Contains("IsKeyboardFocused", StringComparison.Ordinal) &&
              xaml.Contains("IsTextSearchEnabled=\"True\"", StringComparison.Ordinal) &&
              xaml.Contains("MaxDropDownHeight=\"240\"", StringComparison.Ordinal),
            "schematic contains exactly the verified buttons, visible focus treatment and bounded type-ahead key selection");
    }
    private static DeathAdderV3ButtonClient Client(FakeTransport fake, int ms = 3000) => new(fake, TimeSpan.FromMilliseconds(ms));
    private static void Checksum(byte[] frame) => frame[89] = (byte)frame.Skip(3).Take(86).Aggregate(0, (a, b) => a ^ b);
    private static MouseButtonAssignment Assignment(MouseButtonBinding binding)
    {
        var frame = DeathAdderV3ButtonProtocol.CreateBindingSet(4, binding, 1); frame[1] = 2;
        return DeathAdderV3ButtonProtocol.ParseAssignment(frame, 1, 4, 1, true);
    }
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    private static async Task RejectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    private sealed class FakeTransport : IMouseFeatureButtonTransport
    {
        public MouseButtonBinding Current = MouseButtonBinding.Basic(MouseButtonAction.Back);
        public int Writes, Active, Peak;
        public bool BadAck, IgnoreWrite, FailReadback, BlockWrite;
        public Action? OnWrite;
        public Exception? WriteError;
        public TimeSpan Delay;
        public List<byte> Commands { get; } = [];
        public Task<byte[]> ExchangeAsync(ReadOnlyMemory<byte> report, CancellationToken token) => Exchange(report.ToArray(), false, token);
        public Task<byte[]> ExchangeButtonAssignmentAsync(ReadOnlyMemory<byte> report, CancellationToken token) => Exchange(report.ToArray(), true, token);
        private async Task<byte[]> Exchange(byte[] request, bool writing, CancellationToken token)
        {
            var active = Interlocked.Increment(ref Active); Peak = Math.Max(Peak, active); Commands.Add(request[8]);
            try
            {
                await Task.Delay(Delay, token); var response = (byte[])request.Clone(); response[1] = 2;
                if (writing)
                {
                    DeathAdderV3ButtonProtocol.ValidateSetter(request); Writes++; OnWrite?.Invoke(); token.ThrowIfCancellationRequested();
                    if (WriteError is not null) throw WriteError;
                    var parsed = DeathAdderV3ButtonProtocol.ParseAssignment(response, 1, 4, request[2], true);
                    if (!IgnoreWrite) Current = parsed.KnownBinding!;
                    if (BlockWrite) await Task.Delay(Timeout.Infinite, token);
                    if (BadAck) response[15] = 0;
                }
                else if (request[7] == 5)
                {
                    DeathAdderV3ButtonProtocol.ValidateGetter(request);
                    if (request[8] == 0x8A) response[9] = 5;
                    else if (request[8] == 0x80) response[9] = 1;
                    else { response[6] = 2; response[9] = 5; response[10] = 1; }
                }
                else
                {
                    DeathAdderV3ButtonProtocol.ValidateGetter(request);
                    if (FailReadback && Writes > 0) throw new IOException("Synthetic keyboard readback disconnection");
                    var value = request[10] == 4 ? Current : MouseButtonBinding.Basic(MouseButtonAction.LeftClick);
                    response = DeathAdderV3ButtonProtocol.CreateBindingSet(4, value, request[2]);
                    response[1] = 2; response[8] = 0x8C; response[10] = request[10];
                }
                Checksum(response); return response;
            }
            finally { Interlocked.Decrement(ref Active); }
        }
    }
    private sealed class FakeService(FakeTransport transport) : IMouseButtonService
    {
        private readonly DeathAdderV3ButtonClient _client = Client(transport);
        public Task<MouseProfileBanks> ReadProfileBanksAsync(CancellationToken token = default) => _client.ReadProfilesAsync(token);
        public Task<MouseButtonBank> ReadButtonBankAsync(byte profile, CancellationToken token = default) => _client.ReadBankAsync(profile, token);
        public Task<MouseButtonApplyResult> ApplyButtonAsync(MouseButtonAssignment expected, MouseButtonAction action, CancellationToken token = default) => _client.ApplyAsync(expected, action, token);
        public Task<MouseButtonApplyResult> ApplyKeyboardAsync(MouseButtonAssignment expected, MouseKeyboardAssignment assignment, CancellationToken token = default) => _client.ApplyKeyboardAsync(expected, assignment, token);
    }
}
